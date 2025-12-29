<h1 align="center">
  <img src="https://img.shields.io/badge/AI%20기반%20실시간%20차량%2F차선%20감지%20및%20분석%20시스템-24A19C?style=for-the-badge&logo=github&logoColor=white&labelColor=1F2328" alt="프로젝트명 배너" width="100%">
</h1>

## 📌 프로젝트 소개 (Project Introduction)

**슬로건:** **고속도로의 평화 지키미.** (Police/Enforcement Support System)

본 프로젝트는 **경찰차 또는 단속 차량**에 특화된 **지능형 차량 단속 및 증거 기록 시스템(ITS)** 구축을 목표로 합니다. 실시간으로 교통 법규 위반 행위를 감지하고, 사고 및 위반 상황 발생 시 **법적 증거 자료**로 활용될 수 있는 고신뢰성 메타데이터를 기록합니다.

---

## 👥 팀 소개: Error 404: Sleep Not Found

| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" width="200"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" width="200"/> | <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQV4NYGjlzcTvxRY-mJC97tLv_7QrWDQjqNSQ&s" width="300"/> |
| :---: | :---: | :---: |
| **최준영 (PM)** | **김준형** | **김진우** |
| `팀장` | `팀원` | `팀원` |
| [GitHub](https://github.com/ekdlakdl12/Open_CV_Project) | [GitHub](https://github.com/Kim-Junghyeong) | [GitHub](https://github.com/potoblue) |

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
| **백엔드** | <img src="https://img.shields.io/badge/Flask-000000?style=flat-square&logo=flask&logoColor=white"/> | 실시간 차선 인식 API 서버 구동 |
| **딥러닝** | <img src="https://img.shields.io/badge/YOLO-00599C?style=flat-square&logo=yolo&logoColor=white"/> <img src="https://img.shields.io/badge/PyTorch-EE4C2C?style=flat-square&logo=pytorch&logoColor=white"/> | 객체 탐지 및 차선 분할 모델 추론 |
| **이미지 처리** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=flat-square&logo=opencv&logoColor=white"/> | 영상 전처리, 원근 변환(Perspective), 추적 알고리즘 |
| **데이터베이스** | <img src="https://img.shields.io/badge/MongoDB-47A248?style=flat-square&logo=mongodb&logoColor=white"/> | 차량 탐지 이력 및 법적 증거 메타데이터 관리 (NoSQL) |
| **협업/디자인** | <img src="https://img.shields.io/badge/GitHub-181717?style=flat-square&logo=github&logoColor=white"/> <img src="https://img.shields.io/badge/Figma-F24E1E?style=flat-square&logo=figma&logoColor=white"/> | 소스 코드 버전 관리 및 시스템 UI 디자인 |
