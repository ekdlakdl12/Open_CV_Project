import cv2
from ultralytics import YOLO
import json
import argparse
import sys
import time

def run_detection(video_path):
    """
    지정된 비디오 경로에서 YOLOv8을 사용하여 객체를 감지하고
    결과(바운딩 박스 좌표)를 JSON 형식으로 실시간 출력합니다.
    """
    
    try:
        # 'yolov8s.pt' 모델 사용 (인식 정확도 높임)
        model = YOLO('yolov8s.pt') 
    except Exception as e:
        error_output = json.dumps({"status": "error", "message": f"YOLO 모델 로드 실패: {e}"})
        print(error_output, file=sys.stderr, flush=True)
        return

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        error_output = json.dumps({"status": "error", "message": f"비디오 파일 열기 실패: {video_path}"})
        print(error_output, file=sys.stderr, flush=True)
        return

    frame_id = 0
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    start_time = time.time()
    
    while cap.isOpened():
        ret, frame = cap.read()
        if not ret:
            break

        # YOLOv8 추론 (객체 추적 활성화 및 최적화)
        # --- [최종 인식 정확도 및 범위 최대화 설정] ---
        # conf=0.10: 신뢰도 임계값을 대폭 낮춰 (0.20 -> 0.10) 측면/작은 차량까지 강제로 인식 시도
        # iou=0.35: 밀착 차량 분리 유지
        # imgsz=1280: 고해상도 처리 유지
        results = model.track(frame, 
                              persist=True, 
                              tracker='bytetrack.yaml', 
                              verbose=False,
                              conf=0.10,      # <--- [핵심 수정] 신뢰도 임계값 대폭 낮춤
                              iou=0.35,       
                              imgsz=1280,     
                              max_det=300) 
        # ------------------------------------
        
        boxes_data = []
        
        if results and results[0].boxes and results[0].boxes.id is not None:
            boxes = results[0].boxes
            
            for box in boxes:
                # xyxy: xmin, ymin, xmax, ymax
                x_min, y_min, x_max, y_max = [int(x) for x in box.xyxy[0].tolist()]
                conf = round(box.conf[0].item(), 2)
                cls_id = int(box.cls[0].item())
                class_name = model.names[cls_id]

                track_id = int(box.id[0].item()) 

                boxes_data.append({
                    "id": track_id,
                    "class": class_name,
                    "conf": conf,
                    "x_min": x_min,
                    "y_min": y_min,
                    "x_max": x_max,
                    "y_max": y_max,
                })

        frame_output = {
            "type": "frame_data",
            "frame_id": frame_id,
            "total_frames": total_frames,
            "boxes": boxes_data
        }
        
        print(json.dumps(frame_output), flush=True) 

        frame_id += 1

    cap.release()
    
    end_time = time.time()
    total_time = end_time - start_time
    summary_output = {
        "type": "summary",
        "status": "success", 
        "message": f"분석 완료. 총 {frame_id} 프레임 처리.",
        "time": round(total_time, 2),
        "detections": {} 
    }
    print(json.dumps(summary_output), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="YOLOv8 Video Detector for C# Integration")
    parser.add_argument("video_path", help="Path to the input video file")
    
    args = parser.parse_args()
    run_detection(args.video_path)