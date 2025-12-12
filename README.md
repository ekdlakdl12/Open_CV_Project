<h1 align="center">
  <img src="https://img.shields.io/badge/AI%20기반%20실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="1600">
</h1>

## 📌 프로젝트 소개 (Project Introduction)

본 프로젝트는 카메라 영상을 기반으로 실시간으로 도로 상황을 분석하는 **지능형 교통 시스템(ITS)** 구축을 목표로 합니다. 특히, **저해상도 환경**에서도 **딥러닝(SR, CNN/RNN)**과 **OpenCV** 기술을 결합하여 번호판 인식, 차선 유지/이탈 판독, 속도 측정, 그리고 **추돌 위험 감지** 등 복합적인 교통 안전 기능을 제공합니다. 수집된 모든 데이터는 효율적인 교통 관리를 위한 **증거 메타데이터**로 기록됩니다.

### 🎯 핵심 목표

* **정확도 향상:** 저화질 영상에서의 번호판 및 객체 인식 정확도 극대화.
* **실시간 처리:** 영상 스트리밍을 통한 프레임별 실시간 분석 및 위반 감지.
* **안전 확보:** 차량 간 거리(TTC) 분석을 통한 사고 위험 사전 경고.

---

### 👨‍💻 팀 구성 및 역할

| 역할 (Role) | 담당자 (Member) |
| :--- | :--- |
| **팀장 (PM)** | 최준영 |
| **팀원** | 김준형 |
| **팀원** | 김진우 |

---

## 👥 팀 소개

| 📌 팀장 (👑PM) 최준영 | 📌 김준형 | 📌 김진우 |
| :---: | :---: | :---: |
| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" alt="팀장 프로필" width="150"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" alt="김준형 프로필" width="150"/> | <img src="" alt="팀원 프로필" width="150"/> |
| **담당**: 팀장 | **담당**: 팀원 | **담당**: 백엔드 & 핵심 알고리즘 개발 |
| **이메일**: rkfflffpdh12@gmail.com | **이메일**: 916kimjh@naver.com | **이메일**: teamB@example.com |
| **GitHub**: [ekdlakdl12](https://github.com/ekdlakdl12/Open_CV_Project) | **GitHub**: [Kim-Junghyeong](https://github.com/Kim-Junghyeong?tab=repositories) | **GitHub**: [teamB-id](https://github.com/teamB-id) |
| *태어난 김에 사는 개발자.* | *고속도로의 평화 지키미.* | *시스템 통합 및 데이터 처리 담당* |

---

## ✨ 주요 기능 (Key Features)

| No. | 기능 명세 (Functional Specification) | 상세 내용 (핵심 기술 반영) |
| :---: | :--- | :--- |
| 1 | **번호판 인식 (LPR)** | 저화질 환경에서 SR(Super Resolution) 및 CNN/RNN 조합을 통해 번호판 텍스트를 정확하게 인식하고 출력함. |
| 2 | **차선 감지 및 이탈 판독** | **Hough Transform** 또는 **Semantic Segmentation**을 활용하여 차선 경계를 파악하고, 차선 이탈 및 지정차로 위반(버스 전용차로 등)을 판독함. |
| 3 | **차량 속도 측정 및 위반 감지** | **기하학적 투영 변환**을 이용해 실시간 차량 속도를 측정하고, 제한 속도 기준(과속/최저 속도 미만)에 따른 위반 여부를 감지함. |
| 4 | **추돌/사고 위험 감지** | 전방 차량과의 거리와 **TTC (Time To Collision) 분석**을 통해 잠재적 추돌 위험을 예측하고 경고함. |
| 5 | **차종 인식 및 분류** | 딥러닝 모델 (YOLO 등)을 활용하여 차량의 종류(승용차, 트럭, 버스 등)를 자동 분류함. |
| 6 | **증거 메타데이터 기록** | 위반 이벤트 발생 시 **위반 종류, 시간, GPS, 속도, 번호판** 등을 포함하는 정형화된 증거 데이터를 영상과 함께 기록함. |
| 7 | **전방 차량대수 카운팅** | 영상 내 차량 객체를 실시간으로 탐지하여 전방 좌우측 차량대수를 계산함. |
| 8 | **이벤트 기반 녹화** | 위반 및 사고 발생 전후 시점의 고화질 영상 클립을 자동으로 저장함. |

---

## 🛠️ 기술 스택 (Tech Stack)

### 💻 개발 환경 및 기술

| 구분 | 기술 스택 (Tech Stack) | 용도 |
| :--- | :--- | :--- |
| **주요 언어** | <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white"/> <img src="https://img.shields.io/badge/Python-3776AB?style=for-the-badge&logo=python&logoColor=white"/> | 시스템 통합 (C#) 및 핵심 알고리즘 개발 (Python) |
| **딥러닝 프레임워크** | <img src="https://img.shields.io/badge/Yolo-000000?style=for-the-badge&logo=yolo&logoColor=white"/> `딥러닝 라이브러리` | 객체 탐지 및 분류 (차량, 번호판) 및 딥러닝 기반 차선 분할 |
| **이미지/영상 처리** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=for-the-badge&logo=opencv&logoColor=white"/> | 카메라 영상 데이터 전처리, 기하학적 변환, 추적 및 속도/거리 측정 |
| **형상 관리** | <img src="https://img.shields.io/badge/Git-F05032?style=for-the-badge&logo=git&logoColor=white"/> <img src="https://img.shields.io/badge/GitHub-100000?style=for-the-badge&logo=github&logoColor=white"/> | 소스 코드 버전 관리 및 협업 |
| **UX/UI 디자인** | <img src="https://img.shields.io/badge/Figma-F24E1E?style=for-the-badge&logo=figma&logoColor=white"/> | 사용자 인터페이스 디자인 |

---
