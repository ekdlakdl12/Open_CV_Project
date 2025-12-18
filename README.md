<h1 align="center">
  <img src="https://img.shields.io/badge/AI%20기반%20실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="1600">
</h1>

<p align="center">
  <img src="https://img.shields.io/badge/Team-Error%20404:%20Sleep%20Not%20Found-red?style=flat-square&logo=visual-studio-code&logoColor=white"/>
  <br>
  <b>"삽질하느라 잠 못 자는 개발자의 애환을 담은, 끝까지 파고드는 팀"</b>
</p>

## 📌 프로젝트 소개 (Project Introduction)

**슬로건:** **고속도로의 평화 지키미.** (Police/Enforcement Support System)

본 프로젝트는 **경찰차 또는 단속 차량**에 특화된 **지능형 차량 단속 및 증거 기록 시스템(ITS)** 구축을 목표로 합니다.

카메라 영상을 기반으로 실시간으로 교통 법규 위반 행위를 감지하고, 특히 **사고 및 뺑소니, 교통법 위반 상황 발생 시 명확한 증거를 확보**하는 데 중점을 둡니다. **저해상도 환경**에서도 **딥러닝(SR, CNN/RNN)**과 **OpenCV** 기술을 결합하여 번호판 인식, 차선 위반 판독, 속도 측정, 등 복합적인 기능을 제공합니다. 수집된 모든 데이터는 보험 처리의 과실 비율 책정 등 **법적 증거 자료**로 활용될 수 있는 **고신뢰성 메타데이터**로 기록됩니다.

---

## 👥 팀 소개: Error 404: Sleep Not Found

| 📌 팀장 (👑PM) 최준영 | 📌 김준형 | 📌 김진우 |
| :---: | :---: | :---: |
| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" alt="팀장 프로필" width="150"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" alt="김준형 프로필" width="150"/> | <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQV4NYGjlzcTvxRY-mJC97tLv_7QrWDQjqNSQ&s" alt="팀원 프로필" width="250"/> |
| **담당**: 시스템 통합 및 DB | **담당**: AI 모델 및 알고리즘 | **담당**: 영상 처리 및 UI/UX |
| **이메일**: rkfflffpdh12@gmail.com | **이메일**: 916kimjh@naver.com | **이메일**: potoblue@example.com |
| **GitHub**: [ekdlakdl12](https://github.com/ekdlakdl12/Open_CV_Project) | **GitHub**: [Kim-Junghyeong](https://github.com/Kim-Junghyeong) | **GitHub**: [kim-jin-wo](https://github.com/potoblue) |
| *태어난 김에 사는 개발자.* | *고속도로의 평화 지키미.* | *고속도로의 평화 지키미* |

---

## ✨ 주요 기능 (Key Features)

| No. | 기능 명세 (Functional Specification) | 목표 및 단속 상세 내용 |
| :---: | :--- | :--- |
| 1 | **번호판 인식 (LPR)** | **위반 차량 식별** 목표 달성을 위한 **번호판 텍스트의 고정확도 인식** (SR 및 딥러닝 기술 활용). |
| 2 | **차선 감지/전용차로 위반 판독** | **법규 준수 여부 판독** 목표 달성. **지정된 시간 및 구간 외 버스 전용차로 통행 차량** (도로교통법 제61조)의 위반 행위를 감지함. |
| 3 | **차량 속도 측정 및 위반 감지** | **교통 속도 법규 위반 감지** 목표 달성. 실시간 속도 측정 및 **최저 제한 속도 미만 운행 행위** (정체 등 부득이한 상황 제외)를 감지함. |
| 4 | **이벤트 기반 녹화** | **법적 증거 자료 확보** 목표 달성. 위반 행위 및 사고 발생 전후 시점의 고화질 영상 클립을 자동 저장하여 증거 자료로 활용. |
| 5 | **증거 메타데이터 기록** | **위반 차종, 시간, 속도, 번호판** 등 정형화된 증거 데이터 기록. |
| 6 | **차종 인식 및 분류** | 딥러닝 모델을 활용하여 차량의 종류를 자동 분류함. |
| 7 | **전방 및 좌우 차량대수 카운팅** | 영상 내 차량 객체를 실시간으로 탐지하여 전방 좌우측 차량대수를 계산함. |

---

## 🛠️ 기술 스택 (Tech Stack)

### 💻 개발 환경 및 기술

| 구분 | 기술 스택 (Tech Stack) | 용도 |
| :--- | :--- | :--- |
| **주요 언어** | <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white"/> <img src="https://img.shields.io/badge/Python-3776AB?style=for-the-badge&logo=python&logoColor=white"/> | 시스템 통합 (C#) 및 핵심 알고리즘 개발 (Python) |
| **백엔드** | <img src="https://img.shields.io/badge/Flask-000000?style=for-the-badge&logo=flask&logoColor=white"/> | 실시간 차선 인식 및 데이터 통신용 REST API 서버 |
| **딥러닝** | <img src="https://img.shields.io/badge/YOLO-00599C?style=for-the-badge&logo=yolo&logoColor=white"/> <img src="https://img.shields.io/badge/PyTorch-EE4C2C?style=for-the-badge&logo=pytorch&logoColor=white"/> | 객체 탐지 및 분류 (차량, 번호판) 및 딥러닝 기반 차선 분할 |
| **영상 처리** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=for-the-badge&logo=opencv&logoColor=white"/> | 카메라 영상 데이터 전처리, 기하학적 변환, 추적 및 속도/거리 측정 |
| **데이터베이스** | <img src="https://img.shields.io/badge/MySQL-4479A1?style=for-the-badge&logo=mysql&logoColor=white"/> | 차량 탐지 이력, 번호판 텍스트 및 메타데이터 저장/관리 |
| **협업 툴** | <img src="https://img.shields.io/badge/Git-F05032?style=for-the-badge&logo=git&logoColor=white"/> <img src="https://img.shields.io/badge/GitHub-100000?style=for-the-badge&logo=github&logoColor=white"/> <img src="https://img.shields.io/badge/Figma-F24E1E?style=for-the-badge&logo=figma&logoColor=white"/> | 버전 관리 및 UI 디자인 |
