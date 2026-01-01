import cv2
import numpy as np
from flask import Flask, request, jsonify
import base64
import io

app = Flask(__name__)

# ===== 전역 변수: 스무딩을 위한 좌표 히스토리 저장 =====
lane_coords_history = []
MAX_HISTORY_FRAMES = 5  # 최근 5개 프레임의 좌표를 평균합니다.
# =======================================================


# ===== 차선 인식 핵심 로직 함수 (최종 안정화 설정 적용) =====
def process_lane_detection(img_data):
    """
    C#에서 전송된 JPEG 데이터를 받아서 차선 인식 로직을 수행하고,
    스무딩된 8개의 좌표 (x1, y1, x2, y2, x3, y3, x4, y4)를 반환합니다.
    """
    # 1. 바이트 배열을 OpenCV Mat(numpy array)으로 디코딩
    np_array = np.frombuffer(img_data, np.uint8)
    frame = cv2.imdecode(np_array, cv2.IMREAD_COLOR)

    if frame is None:
        return None

    h, w = frame.shape[:2]
    
    # ----------------------------------------------------
    # 1) Perspective Transform 설정 (왼쪽 경계 및 Y 좌표 최종 안정화)
    # SrcX1/SrcX3를 중앙으로 좁혀 왼쪽 노이즈 유입을 차단합니다. (42%/10%)
    SrcX1 = 0.42; SrcY1 = 0.70;  # Left Top X/Y 좌표
    SrcX2 = 0.60; SrcY2 = 0.70;  # Right Top X/Y 좌표
    SrcX3 = 0.10; SrcY3 = 1.0;   # Left Bottom X/Y 좌표
    SrcX4 = 0.95; SrcY4 = 1.0;   
    
    DstOffset = 0.2;
    
    src_points = np.float32([
        [w * SrcX1, h * SrcY1],
        [w * SrcX2, h * SrcY2],
        [w * SrcX4, h * SrcY4],
        [w * SrcX3, h * SrcY3]
    ])
    dst_points = np.float32([
        [w * DstOffset, 0],
        [w * (1 - DstOffset), 0],
        [w * (1 - DstOffset), h],
        [w * DstOffset, h]
    ])

    M = cv2.getPerspectiveTransform(src_points, dst_points)
    Minv = cv2.getPerspectiveTransform(dst_points, src_points)
    
    # 2) Warp Perspective & HLS 필터링
    warped = cv2.warpPerspective(frame, M, (w, h), flags=cv2.INTER_LINEAR)
    hls = cv2.cvtColor(warped, cv2.COLOR_BGR2HLS)
    
    # 흰색, 노란색 차선 마스크 생성
    white_mask = cv2.inRange(hls, np.array([0, 200, 0]), np.array([255, 255, 255]))
    yellow_mask = cv2.inRange(hls, np.array([15, 30, 80]), np.array([45, 255, 255]))
    combined_mask = cv2.bitwise_or(white_mask, yellow_mask)
    
    # 3) 차선 픽셀 추출 및 히스토그램 분석
    lane_pixels = cv2.findNonZero(combined_mask)
    
    if lane_pixels is None or len(lane_pixels) < 100:
        unwarped_coords = cv2.perspectiveTransform(dst_points[None, :, :], Minv)[0]
    else:
        all_x = lane_pixels[:, 0, 0]
        all_y = lane_pixels[:, 0, 1]
        
        # 히스토그램을 이미지 하단 1/3 영역에서만 계산
        mid_h = h * 2 // 3  
        bottom_third_pixels = combined_mask[mid_h:, :]
        histogram = np.sum(bottom_third_pixels, axis=0)

        midpoint = w // 2
        left_hist = histogram[:midpoint]
        right_hist = histogram[midpoint:]

        left_x_base = np.argmax(left_hist)
        right_x_base = np.argmax(right_hist) + midpoint

        # 4) 슬라이딩 윈도우 및 FitLine 적용
        n_windows = 9
        window_height = h // n_windows
        margin = 50
        minpix = 50

        left_lane_inds = []
        right_lane_inds = []
        
        left_x_current = left_x_base
        right_x_current = right_x_base

        for window in range(n_windows):
            win_y_low = h - (window + 1) * window_height
            win_y_high = h - window * window_height

            good_left_inds = ((all_y >= win_y_low) & (all_y < win_y_high) & 
                              (all_x >= left_x_current - margin) & (all_x < left_x_current + margin)).nonzero()[0]
            good_right_inds = ((all_y >= win_y_low) & (all_y < win_y_high) & 
                               (all_x >= right_x_current - margin) & (all_x < right_x_current + margin)).nonzero()[0]
            
            if len(good_left_inds) > minpix:
                left_x_current = np.int64(np.mean(all_x[good_left_inds]))
            if len(good_right_inds) > minpix:
                right_x_current = np.int64(np.mean(all_x[good_right_inds]))
                
            left_lane_inds.append(good_left_inds)
            right_lane_inds.append(good_right_inds)

        left_lane_inds = np.concatenate(left_lane_inds)
        right_lane_inds = np.concatenate(right_lane_inds)

        left_x = all_x[left_lane_inds]
        left_y = all_y[left_lane_inds]
        right_x = all_x[right_lane_inds]
        right_y = all_y[right_lane_inds]
        
        # 5) FitLine (직선 근사)
        # Left Lane
        if len(left_x) > 100:
            left_points = np.vstack((left_x, left_y)).T.astype(np.int32)
            [lvx, lvy, lx0, ly0] = cv2.fitLine(left_points, cv2.DIST_L2, 0, 0.01, 0.01)
            left_y1 = h # Bottom
            left_x1 = int(lx0 + (left_y1 - ly0) * lvx / lvy)
            left_y2 = 0 # Top
            left_x2 = int(lx0 + (left_y2 - ly0) * lvx / lvy)
        else:
            left_x1 = int(w * DstOffset) 
            left_x2 = int(w * DstOffset) 
            left_y1 = h
            left_y2 = 0
            
        # Right Lane
        if len(right_x) > 100:
            right_points = np.vstack((right_x, right_y)).T.astype(np.int32)
            [rvx, rvy, rx0, ry0] = cv2.fitLine(right_points, cv2.DIST_L2, 0, 0.01, 0.01)
            right_y1 = h # Bottom
            right_x1 = int(rx0 + (right_y1 - ry0) * rvx / rvy)
            right_y2 = 0 # Top
            right_x2 = int(rx0 + (right_y2 - ry0) * rvx / rvy)
        else:
            right_x1 = int(w * (1 - DstOffset)) 
            right_x2 = int(w * (1 - DstOffset)) 
            right_y1 = h
            right_y2 = 0
            
        # 6) Inverse Perspective Transform에 사용할 4개 좌표
        lane_polygon = np.float32([
            [left_x1, left_y1],   # Left Bottom (LB)
            [left_x2, left_y2],   # Left Top (LT)
            [right_x2, right_y2], # Right Top (RT)
            [right_x1, right_y1]  # Right Bottom (RB)
        ])
        unwarped_coords = cv2.perspectiveTransform(lane_polygon[None, :, :], Minv)[0]

    # 7) 현재 프레임의 C# 형식 좌표 리스트
    current_coords_list = []
    current_coords_list.append(int(unwarped_coords[1][0])); current_coords_list.append(int(unwarped_coords[1][1])) # p1 (Left Top)
    current_coords_list.append(int(unwarped_coords[2][0])); current_coords_list.append(int(unwarped_coords[2][1])) # p2 (Right Top)
    current_coords_list.append(int(unwarped_coords[3][0])); current_coords_list.append(int(unwarped_coords[3][1])) # p3 (Right Bottom)
    current_coords_list.append(int(unwarped_coords[0][0])); current_coords_list.append(int(unwarped_coords[0][1])) # p4 (Left Bottom)

    
    # 8) 스무딩 적용: 좌표 히스토리에 현재 좌표를 추가하고 평균 계산
    global lane_coords_history
    
    lane_coords_history.append(current_coords_list)
    
    if len(lane_coords_history) > MAX_HISTORY_FRAMES:
        lane_coords_history.pop(0)

    if len(lane_coords_history) > 0:
        avg_coords = np.mean(lane_coords_history, axis=0).astype(int).tolist()
    else:
        avg_coords = current_coords_list
        
    return avg_coords


# ===== Flask 서버 설정 =====
@app.route('/detect_lane', methods=['POST'])
def detect_lane():
    if 'frame' not in request.files:
        return jsonify({'status': 'error', 'message': 'No frame part in the request'}), 400

    file = request.files['frame']
    img_data = file.read()

    try:
        lane_coords = process_lane_detection(img_data)
        
        if lane_coords is None:
            return jsonify({'status': 'error', 'message': 'Failed to decode image or frame is empty'}), 500
        
        return jsonify({'status': 'success', 'lane_coords': lane_coords})

    except Exception as e:
        import traceback
        return jsonify({'status': 'error', 'message': f'Processing error: {str(e)}\n{traceback.format_exc()}'}), 500


if __name__ == '__main__':
    print("Starting Flask server on http://127.0.0.1:5000/...")
    # debug=False로 설정하여 Flask 기본 로그 출력 수를 줄일 수 있지만, 
    # 현재는 C# 클라이언트의 전송 횟수를 줄이는 것이 더 효과적입니다.
    app.run(host='127.0.0.1', port=5000, debug=True)