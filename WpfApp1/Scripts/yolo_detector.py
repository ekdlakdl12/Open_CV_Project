import sys
import json
import cv2
from ultralytics import YOLO

# ----------------------------------------------------
# 1. 입력 파라미터 확인 및 모델 로딩
# ----------------------------------------------------
def analyze_video(video_path):
    """
    주어진 경로의 영상을 YOLOv8로 분석하고 결과를 반환합니다.
    """
    try:
        # YOLOv8 모델 로드 (pre-trained weights)
        # 실제 사용 시, 모델 파일 경로를 지정해야 합니다. (예: 'yolov8n.pt')
        model = YOLO('yolov8n.pt') 

        # OpenCV를 사용하여 영상 파일 열기
        cap = cv2.VideoCapture(video_path)
        if not cap.isOpened():
            return {"error": f"Error opening video file: {video_path}"}
        
        # 인식된 객체의 종류별 카운트 저장
        detection_counts = {}
        
        frame_count = 0
        
        while cap.isOpened():
            ret, frame = cap.read()
            if not ret:
                break
            
            # --- [핵심] YOLOv8 인식 수행 ---
            # stream=True로 설정하여 메모리 효율성을 높일 수 있습니다.
            results = model(frame, verbose=False) 
            
            for result in results:
                # result.boxes: 바운딩 박스 정보
                # result.names: 클래스 이름 딕셔너리
                # result.boxes.cls: 감지된 객체의 클래스 ID (Tensor)

                for box in result.boxes:
                    class_id = int(box.cls.cpu().numpy()[0])
                    class_name = result.names[class_id]
                    
                    # 원하는 객체만 필터링 (선택 사항)
                    # if class_name in ['car', 'truck', 'person', 'traffic sign']:
                        
                    detection_counts[class_name] = detection_counts.get(class_name, 0) + 1
                    
            frame_count += 1
            # 고속으로 처리하기 위해 전체 프레임을 다 분석하지 않고 일부만 샘플링할 수 있습니다.
            # if frame_count % 10 != 0: continue 

        cap.release()

        # 결과 정리: 누적 카운트를 프레임 수로 나누어 평균 개수를 낼 수도 있습니다.
        # 여기서는 전체 누적 카운트를 반환합니다.
        
        # ----------------------------------------------------
        # 2. 결과 포맷팅 및 표준 출력 (C#로 전달)
        # ----------------------------------------------------
        
        # C#에서 쉽게 파싱할 수 있도록 JSON 형태로 변환
        output_data = {
            "status": "success",
            "total_frames": frame_count,
            "detections": detection_counts
        }
        
        return output_data

    except Exception as e:
        # 오류 발생 시 오류 메시지 출력 후 종료
        return {"status": "error", "message": str(e)}

# --- 메인 실행부 ---
if __name__ == "__main__":
    if len(sys.argv) < 2:
        # 영상 경로가 인수로 제공되지 않은 경우
        output = {"status": "error", "message": "Usage: python yolo_detector.py <video_path>"}
    else:
        # 첫 번째 인수가 영상 경로
        video_file_path = sys.argv[1]
        output = analyze_video(video_file_path)

    # 결과를 표준 출력 (stdout)으로 내보냄
    print(json.dumps(output))