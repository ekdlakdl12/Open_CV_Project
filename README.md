<h1 align="center">
  <img src="https://img.shields.io/badge/AI%20기반%20실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="1600">
</h1>

## 📌 프로젝트 소개 (Project Introduction)

본 프로젝트는 카메라 영상을 기반으로 실시간으로 도로 상황을 분석하는 **지능형 교통 시스템(ITS)** 구축을 목표로 합니다. 고속도로 및 일반 도로에서 발생하는 다양한 차량 데이터를 딥러닝 기술을 활용하여 수집 및 분석함으로써 안전하고 효율적인 교통 관리에 기여합니다.

---

## ✨ 주요 기능 (Key Features)

| No. | 기능 명세 (Functional Specification) | 상세 내용 |
| :---: | :--- | :--- |
| 1 | **실시간 영상 처리** | 카메라를 통한 영상 스트리밍 및 프레임별 데이터 분석 |
| 2 | **차선 감지 및 이탈 판독** | 주행 중인 차선을 정확하게 인식하고, 차선 이탈 시 경고 기능 제공 |
| 3 | **번호판 인식 (LPR)** | 차량의 번호판 영역을 탐지하고 문자를 인식 |
| 4 | **차종 인식 및 분류** | 딥러닝 모델을 활용하여 차량의 종류(승용차, 트럭, 버스 등) 자동 분류 |
| 5 | **차량 속도 측정** | 영상 분석을 통해 실시간 차량 주행 속도 측정 및 기록 |
| 6 | **차 간격 측정** | 전방 차량과의 안전 거리(차 간격) 실시간 측정 |

---

## 🛠️ 기술 스택 (Tech Stack)

### 💻 개발 환경 및 기술

| 구분 | 기술 스택 (Tech Stack) | 용도 |
| :--- | :--- | :--- |
| **주요 언어** | <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white"/> <img src="https://img.shields.io/badge/Python-3776AB?style=for-the-badge&logo=python&logoColor=white"/> | 시스템 통합 및 핵심 알고리즘 개발 |
| **딥러닝 프레임워크** | <img src="https://img.shields.io/badge/Yolo-000000?style=for-the-badge&logo=yolo&logoColor=white"/> `딥러닝 라이브러리` | 객체 탐지 및 인식 (번호판, 차종) |
| **이미지/영상 처리** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=for-the-badge&logo=opencv&logoColor=white"/> | 카메라 영상 데이터 처리 및 전처리 |
| **형상 관리** | <img src="https://img.shields.io/badge/Git-F05032?style=for-the-badge&logo=git&logoColor=white"/> <img src="https://img.shields.io/badge/GitHub-100000?style=for-the-badge&logo=github&logoColor=white"/> | 소스 코드 버전 관리 및 협업 |
| **UX/UI 디자인** | <img src="https://img.shields.io/badge/Figma-F24E1E?style=for-the-badge&logo=figma&logoColor=white"/> | 화면 구성 및 사용자 인터페이스 디자인 |

### 👨‍💻 팀 구성 및 역할

| 역할 (Role) | 담당자 (Member) |
| :--- | :--- |
| **팀장 (PM)** | 이름 없음 |
| **팀원** | 김준형 |
| **팀원** | 팀원 B |

---

## 👥 팀 소개

| 📌 팀장 (👑PM) | 📌 김준형 | 📌 팀원 B |
| :---: | :---: | :---: |
| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" alt="팀장 프로필" width="150"/> | <img src="[김준형 프로필 이미지 링크]" alt="김준형 프로필" width="150"/> | <img src="[팀원 B 프로필 이미지 링크]" alt="팀원 B 프로필" width="150"/> |
| **담당**: 팀장 | **담당**: 팀원 | **담당**: 백엔드 & 핵심 알고리즘 개발 |
| **이메일**: rkfflffpdh12@gmail.com | **이메일**: 916kimjh@naver.com | **이메일**: teamB@example.com |
| **GitHub**: [ekdlakdl12](https://github.com/ekdlakdl12/Open_CV_Project) | **GitHub**: [Kim-Junghyeong](https://github.com/Kim-Junghyeong?tab=repositories) | **GitHub**: [teamB-id](https://github.com/teamB-id) |
| *태어난 김에 사는 개발자.* | *고속도로의 평화 지키미.* | *시스템 통합 및 데이터 처리 담당* |

---

## 🚀 시작하는 방법 (Getting Started)

### 1. 저장소 클론

```bash
# GitHub 저장소 클론
git clone [YOUR_REPOSITORY_URL]
cd [PROJECT_NAME]
