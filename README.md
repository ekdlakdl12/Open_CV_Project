<h1 align="center">
  <img src="https://img.shields.io/badge/AI%20기반%20실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="1600">
</h1>

## 📌 프로젝트 소개 (Project Introduction)

**슬로건:** **고속도로의 평화 지키미.** (Police/Enforcement Support System)

본 프로젝트는 **경찰차 또는 단속 차량**에 특화된 **지능형 차량 단속 및 증거 기록 시스템(ITS)** 구축을 목표로 합니다.


카메라 영상을 기반으로 실시간으로 교통 법규 위반 행위를 감지하고, 특히 **사고 및 뺑소니 상황 발생 시 명확한 영상 증거를 확보**하는 데 중점을 둡니다. **저해상도 환경**에서도 **딥러닝(SR, CNN/RNN)**과 **OpenCV** 기술을 결합하여 번호판 인식, 차선 위반 판독, 속도 측정, 그리고 **추돌 위험 감지** 등 복합적인 기능을 제공합니다. 수집된 모든 데이터는 보험 처리의 과실 비율 책정 등 **법적 증거 자료**로 활용될 수 있는 **고신뢰성 메타데이터**로 기록됩니다.

### 🎯 핵심 목표

* **단속 및 수사 지원:** 경찰차/단속차량에 탑재되어 실시간으로 위반 차량을 식별하고 단속 효율을 극대화.
* **증거 기록의 명확성:** 사고 발생 시, 명확한 영상 및 정형화된 메타데이터를 제공하여 **뺑소니 검거, 과실 비율(몇 대 몇) 책정** 등 법적 근거 자료로 활용.
* **운전자 안전 확보:** 차량 간 거리(TTC) 분석을 통한 사고 위험 사전 경고 기능 제공.
* **정확도 향상:** 저화질 영상에서의 번호판 및 객체 인식 정확도 극대화.

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
| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" alt="팀장 프로필" width="150"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" alt="김준형 프로필" width="150"/> | <img src="data:image/jpeg;base64,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" alt="팀원 프로필" width="150"/> |
| **담당**: 팀장 | **담당**: 팀원 | **담당**: 팀원 |
| **이메일**: rkfflffpdh12@gmail.com | **이메일**: 916kimjh@naver.com | **이메일**: https://github.com/potoblue?tab=repositories |
| **GitHub**: [ekdlakdl12](https://github.com/ekdlakdl12/Open_CV_Project) | **GitHub**: [Kim-Junghyeong](https://github.com/Kim-Junghyeong?tab=repositories) | **GitHub**: [kim-jin-u](https://github.com/potoblue?tab=repositories) |
| *태어난 김에 사는 개발자.* | *고속도로의 평화 지키미.* | *고속도로의 평화 지키미* |

---

## ✨ 주요 기능 (Key Features)

| No. | 기능 명세 (Functional Specification) | 목표 및 단속 상세 내용 |
| :---: | :--- | :--- |
| 1 | **번호판 인식 (LPR)** | **위반 차량 식별** 목표 달성을 위한 **번호판 텍스트의 고정확도 인식** (SR 및 딥러닝 기술 활용). |
| 2 | **차선 감지/전용차로 위반 판독** | **법규 준수 여부 판독** 목표 달성. **지정된 시간 및 구간 외 버스 전용차로 통행 차량** (도로교통법 제61조)의 위반 행위를 감지함. |
| 3 | **차량 속도 측정 및 위반 감지** | **교통 속도 법규 위반 감지** 목표 달성. 실시간 속도 측정 및 **최저 제한 속도 미만 운행 행위** (정체 등 부득이한 상황 제외)를 감지함. |
| 4 | **이벤트 기반 녹화** | **법적 증거 자료 확보** 목표 달성. 위반 행위 및 사고 발생 전후 시점의 고화질 영상 클립을 자동 저장하여 증거 자료로 활용. |
| 5 | **추돌/사고 위험 감지** | 전방 차량과의 거리와 TTC 분석을 통해 잠재적 추돌 위험을 예측하고 경고함. |
| 6 | **증거 메타데이터 기록** | 영상과 함께 **위반 종류, 시간, GPS, 속도, 번호판** 등 정형화된 증거 데이터 기록. |
| 7 | **차종 인식 및 분류** | 딥러닝 모델을 활용하여 차량의 종류를 자동 분류함. |
| 8 | **전방 차량대수 카운팅** | 영상 내 차량 객체를 실시간으로 탐지하여 전방 좌우측 차량대수를 계산함. |

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
