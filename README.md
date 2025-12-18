<h1 align="center">
  <br>
  🚗 AI-ITS: 실시간 차량 및 차선 분석 시스템 🔍
  <br>
</h1>

<p align="center">
  <b>"고속도로의 평화 지키미: 경찰 및 단속 차량을 위한 지능형 증거 기록 시스템"</b>
  <br>
  <img src="https://img.shields.io/badge/Team-Error%20404:%20Sleep%20Not%20Found-E01E5A?style=for-the-badge&logo=visual-studio-code&logoColor=white"/>
</p>

<p align="center">
  <i>"끝없는 삽질로 완성도를 파고드는, 밤샘 개발자들의 집합"</i>
</p>

<p align="center">
  <img src="https://user-images.githubusercontent.com/placeholder-lane-detection-demo.gif" width="80%" alt="Project Demo Preview"/>
</p>

---

## 📌 프로젝트 소개 (Project Introduction)

본 프로젝트는 **경찰차 또는 단속 차량**에 특화된 **지능형 차량 단속 및 증거 기록 시스템(ITS)** 구축을 목표로 합니다. 카메라 영상을 기반으로 실시간 교통 법규 위반 행위를 감지하고, 사고 및 위반 상황 발생 시 **법적 증거 자료**로 활용될 수 있는 고신뢰성 메타데이터를 기록합니다.

### 🎯 핵심 목표
* **✅ 단속 지원:** 실시간 위반 차량 식별 및 단속 효율 극대화
* **✅ 증거 확보:** 뺑소니 검거 및 과실 비율 책정을 위한 명확한 데이터 제공
* **✅ 안전 확보:** 차량 간 거리(TTC) 분석을 통한 사고 위험 사전 경고
* **✅ 정밀 인식:** 저화질 환경에서도 딥러닝(SR)을 통한 번호판 인식률 극대화

---

## 👥 팀 소개 (Team Information)

| <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTlvx9kKgphtqxjtyo02-hkg1Vc3retA_F-Ow&s" width="100%"/> | <img src="https://i.pinimg.com/736x/cd/29/5b/cd295b740eb04c3ec4acdd6cb4f11f47.jpg" width="100%"/> | <img src="https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcQV4NYGjlzcTvxRY-mJC97tLv_7QrWDQjqNSQ&s" width="100%"/> |
| :---: | :---: | :---: |
| **👑 최준영 (PM)** | **💻 김준형** | **🎨 김진우** |
| `System Architecture` | `AI Algorithm` | `Image Processing` |
| [![GitHub](https://img.shields.io/badge/GitHub-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/ekdlakdl12/Open_CV_Project) | [![GitHub](https://img.shields.io/badge/GitHub-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/Kim-Junghyeong) | [![GitHub](https://img.shields.io/badge/GitHub-181717?style=flat-square&logo=github&logoColor=white)](https://github.com/potoblue) |

---

## 🛣️ 차선 인식 알고리즘 원리 (Lane Detection Logic)

공간 활용을 위해 시스템의 핵심 로직을 시각화 단계로 구성했습니다.



1. **Bird's Eye View:** `Perspective Transform`을 사용하여 전방 영상을 위에서 내려다보는 시점으로 변환.
2. **Color Filtering:** `HLS` 색공간에서 흰색과 노란색 차선 마스크를 생성하여 노이즈 제거.
3. **Sliding Window:** 히스토그램 분석으로 차선 위치를 추정하고 윈도우를 쌓아 차선 픽셀 추출.
4. **FitLine & Smoothing:** 직선 근사 및 프레임 간 좌표 평균화(`Moving Average`)로 떨림 방지.

---

## ✨ 주요 기능 (Key Features)

> #### 🔢 01. 번호판 인식 (LPR)
> SR(Super Resolution) 및 딥러닝 기술을 활용하여 저해상도 영상에서도 고정밀 번호판 텍스트 추출.

> #### 🛣️ 02. 차선 감지 및 위반 판독
> 버스 전용차로 준수 여부 및 실시간 차선 이탈 위반 감지 (도로교통법 제61조 준거).

> #### ⚡ 03. 속도 측정 및 위반 감지
> 실시간 차량 객체 추적을 통한 속도 계산 및 최저 제한 속도 미만/과속 운행 판독.

> #### 📹 04. 이벤트 기반 자동 녹화
> 사고 및 법규 위반 의심 상황 전후 시점의 영상을 증거 자료용 클립으로 자동 추출 및 저장.

---

## 🛠️ 기술 스택 (Tech Stack)

### 💻 Development & Environment

| Category | Stack |
| :--- | :--- |
| **Languages** | <img src="https://img.shields.io/badge/C%23-239120?style=for-the-badge&logo=c-sharp&logoColor=white"/> <img src="https://img.shields.io/badge/Python-3776AB?style=for-the-badge&logo=python&logoColor=white"/> |
| **Backend** | <img src="https://img.shields.io/badge/Flask-000000?style=for-the-badge&logo=flask&logoColor=white"/> |
| **Deep Learning** | <img src="https://img.shields.io/badge/YOLO-00599C?style=for-the-badge&logo=yolo&logoColor=white"/> <img src="https://img.shields.io/badge/PyTorch-EE4C2C?style=for-the-badge&logo=pytorch&logoColor=white"/> |
| **Vision** | <img src="https://img.shields.io/badge/OpenCV-5C3EE8?style=for-the-badge&logo=opencv&logoColor=white"/> |
| **Database** | <img src="https://img.shields.io/badge/MySQL-4479A1?style=for-the-badge&logo=mysql&logoColor=white"/> |
| **Collaboration** | <img src="https://img.shields.io/badge/GitHub-181717?style=for-the-badge&logo=github&logoColor=white"/> <img src="https://img.shields.io/badge/Figma-F24E1E?style=for-the-badge&logo=figma&logoColor=white"/> |
