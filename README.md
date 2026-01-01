<h1 align="center">
  <img src="https://img.shields.io/badge/실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="100%">
</h1>

## 📌 프로젝트 소개 (Project Introduction)

**슬로건:** **고속도로의 평화 지키미.** (Police/Enforcement Support System)

본 프로젝트는 **경찰차 또는 단속 차량**에 특화된 **지능형 차량 단속 및 증거 기록 시스템(ITS)** 구축을 목표로 합니다.  
실시간으로 교통 법규 위반 행위를 감지하고, 사고 및 위반 상황 발생 시 **법적 증거 자료**로 활용될 수 있는 고신뢰성 메타데이터를 기록합니다.

---

## 👥 팀 소개: Error 404: Sleep Not Found

| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" width="200"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" width="200"/> | <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQV4NYGjlzcTvxRY-mJC97tLv_7QrWDQjqNSQ&s" width="200"/> |
| :---: | :---: | :---: |
| **최준영 (PM)** | **김준형** | **김진우** |
| `팀장` | `팀원` | `팀원` |
| [GitHub](https://github.com/ekdlakdl12/Open_CV_Project) | [GitHub](https://github.com/Kim-Junghyeong) | [GitHub](https://github.com/potoblue) |
| `전방 좌우측 차량대수, 상대속도측정, DB` | `차선 감지 및 위반 판독, PPT, 발표` | `번호판 인식 기능` |
---

## ✨ 주요 기능 (Key Features)

| No. | 기능 명세 | 상세 내용 |
| :---: | :--- | :--- |
| 1 | **번호판 인식 (LPR)** | SR 및 딥러닝 기술을 활용한 고정밀 번호판 텍스트 추출. |
| 2 | **차선 감지 및 위반 판독** | 버스 전용차로 및 차선 이탈 위반 실시간 감지 (도로교통법 제61조 기준). |
| 3 | **속도 측정 및 위반 감지** | 실시간 차량 속도 계산 및 과속/저속 주행 위반 판독. |
| 4 | **증거 메타데이터 관리** | 위반 차종, 시간, 속도, 번호판 정보를 DB에 정형화하여 기록. |

---

## 🛠️ 기술 스택 (Tech Stack)

### 💻 개발 환경 및 기술

| 구분 | 기술 스택 (Tech Stack) | 용도 |
| :--- | :--- | :--- |
| **주요 언어** | <img src="https://img.shields.io/badge/C%23-239120?style=flat-square&logo=c-sharp&logoColor=white"/> <img src="https://img.shields.io/badge/Python-3776AB?style=flat-square&logo=python&logoColor=white"/> | UI/UX 통합 및 코어 알고리즘 구현 |
| **딥러닝** | <img src="https://img.shields.io/badge/YOLO-00599C?style=flat-square&logo=yolo&logoColor=white"/> <img src="https://img.shields.io/badge/PyTorch-EE4C2C?style=flat-square&logo=pytorch&logoColor=white"/> | 객체 탐지 및 차선 분할 모델 , 차종 학습 모델 |
| **이미지 처리** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=flat-square&logo=opencv&logoColor=white"/> | 영상 전처리, 원근 변환(Perspective), 추적 알고리즘 |
| **데이터베이스** | <img src="https://img.shields.io/badge/MongoDB-47A248?style=flat-square&logo=mongodb&logoColor=white"/> | 차량 탐지 이력 및 법적 증거 메타데이터 관리 |
| **협업** | <img src="https://img.shields.io/badge/GitHub-181717?style=flat-square&logo=github&logoColor=white"/> | 소스 코드 버전 관리 |


---

### 프로젝트 구조  

```
WpfApp1  
├─ Models  
│  ├─ CarModelData.cs        # 차량 기본 정보 모델  
│  ├─ Detection.cs           # YOLO 탐지 결과 데이터 구조  
│  ├─ TrackedObject.cs       # 추적 중인 객체 정보  
│  └─ VehicleRecord.cs       # 위반 차량 기록용 모델  
│
├─ Script
│  ├─ LaneAnalyzer.cs        # 차선 인식 및 위반 판단 로직  
│  ├─ YoloOnnx.cs            # YOLO ONNX 모델 로딩 및 추론  
│  └─ YoloV8Onnx.cs          # YOLOv8 전용 추론 클래스  
│
├─ Scripts
│  ├─ best.onnx              # 학습된 YOLO 모델  
│  ├─ yolop-640-640.onnx     # 차선 인식용 모델  
│  └─ yolov8n.onnx           # 경량 YOLOv8 모델  
│
├─ Services
│  ├─ VideoPlayerService.cs  # 영상 재생 및 프레임 관리  
│  ├─ YoloDetectService.cs   # YOLO 기반 객체 탐지 서비스  
│  └─ YoloPDetectService.cs  # YOLOP 차선 인식 서비스  
│
├─ ViewModels
│  ├─ MainViewModel.cs       # 메인 화면 상태 관리  
│  ├─ MainWindowViewModel.cs # MainWindow 전용 ViewModel  
│  ├─ RelayCommand.cs        # MVVM 커맨드 구현  
│  └─ ViewModelBase.cs       # ViewModel 공통 베이스  
│
├─ Views
│  ├─ MainWindow.xaml        # 메인 UI 화면  
│  └─ MainWindow.xaml.cs     # UI 이벤트 코드 비하인드  
│
├─ App.xaml                  # WPF 애플리케이션 설정  
├─ AssemblyInfo.cs  
│
├─ PY_lane_server.py         # Python 기반 차선 인식 서버  
├─ Right/                   # 테스트용 리소스  
└─ win_y_low/                # 실험 데이터 디렉토리

```
---

## ✨ 사용한 NuGet 패키지
[NuGet 명세서 바로가기](https://docs.google.com/spreadsheets/d/1hhw3pMnT4UuUW-MDNwfc8PLnBW5Qk5wP7uosV7TMYpw/edit?gid=2004713425#gid=2004713425) 

## ✨ 프로젝트 일정표
[프로젝트 일정표](https://docs.google.com/spreadsheets/d/1GLfk-Re6UQ8nnsiVm0ssotUfYDmXTo-F14aOCp_t6cc/edit?gid=1123180974#gid=1123180974) 

## ✨ 발생했던 이슈 정리
[issue](https://docs.google.com/spreadsheets/d/1aEBCMos3K2WiVfYiYnP1RT7b2Q9AzO9n2q2SfhDSc-g/edit?gid=1092058707#gid=1092058707) 

---


