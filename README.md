# 🛣️ AI 기반 실시간 차량/차선 감지 및 분석 시스템

[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/YOUR_GITHUB_ID/YOUR_REPO_NAME?style=social)](https://github.com/YOUR_GITHUB_ID/YOUR_REPO_NAME)
[![GitHub forks](https://img.shields.io/github/forks/YOUR_GITHUB_ID/YOUR_REPO_NAME?style=social)](https://github.com/YOUR_GITHUB_ID/YOUR_REPO_NAME)

## 📌 프로젝트 소개 (Project Introduction)

본 프로젝트는 카메라 영상을 기반으로 실시간으로 도로 상황을 분석하는 **지능형 교통 시스템(ITS)** 구축을 목표로 합니다. 고속도로 및 일반 도로에서 발생하는 다양한 차량 데이터를 딥러닝 기술을 활용하여 수집 및 분석함으로써 안전하고 효율적인 교통 관리에 기여합니다.

---

## ✨ 주요 기능 (Key Features)

제시된 '기능 명세서'를 바탕으로 주요 기능을 정의했습니다.

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

제시된 기술 스택을 기반으로 역할별 구분을 명확히 했습니다.

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
| **UX/UI & 프론트엔드** | 최준영 |
| **백엔드 & 핵심 알고리즘 개발** | 3명 (팀원 A, B, C) |

---

## 👥 팀 소개

<div style="display: flex; flex-wrap: wrap; justify-content: space-between; gap: 20px;">
    
    <div style="flex: 1 1 45%; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px; box-shadow: 2px 2px 5px rgba(0,0,0,0.05);">
        <h3 style="margin-top: 0;"><img src="https://img.shields.io/badge/Pin-E83E8C?style=flat-square&logo=pinboard&logoColor=white" alt="Pin"/> 최준영 (👑PM)</h3>
        <div style="border: 1px dashed #cccccc; padding: 40px; margin-bottom: 15px; text-align: center; color: #888;">[프로필 이미지 링크]</div>
        <ul style="padding-left: 20px;">
            <li>**담당**: UX/UI & 프론트엔드</li>
            <li>**이메일**: junyoung@example.com</li>
            <li>**깃허브 아이디 (레포지토리 링크)**: [jychannel](https://github.com/jychannel)</li>
            <li>**자유롭게 소개**: 사용자 경험을 설계하고 프론트엔드 개발을 담당합니다.</li>
        </ul>
    </div>

    <div style="flex: 1 1 45%; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px; box-shadow: 2px 2px 5px rgba(0,0,0,0.05);">
        <h3 style="margin-top: 0;"><img src="https://img.shields.io/badge/Pin-E83E8C?style=flat-square&logo=pinboard&logoColor=white" alt="Pin"/> 팀원 A</h3>
        <div style="border: 1px dashed #cccccc; padding: 40px; margin-bottom: 15px; text-align: center; color: #888;">[프로필 이미지 링크]</div>
        <ul style="padding-left: 20px;">
            <li>**담당**: 백엔드 & 핵심 알고리즘 개발</li>
            <li>**이메일**: teamA@example.com</li>
            <li>**깃허브 아이디 (레포지토리 링크)**: [teamA-id](https://github.com/teamA-id)</li>
            <li>**자유롭게 소개**: 차량 감지 및 분석 알고리즘 구현을 담당합니다.</li>
        </ul>
    </div>
    
    <div style="flex: 1 1 45%; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px; box-shadow: 2px 2px 5px rgba(0,0,0,0.05);">
        <h3 style="margin-top: 0;"><img src="https://img.shields.io/badge/Pin-E83E8C?style=flat-square&logo=pinboard&logoColor=white" alt="Pin"/> 팀원 B</h3>
        <div style="border: 1px dashed #cccccc; padding: 40px; margin-bottom: 15px; text-align: center; color: #888;">[프로필 이미지 링크]</div>
        <ul style="padding-left: 20px;">
            <li>**담당**: 백엔드 & 핵심 알고리즘 개발</li>
            <li>**이메일**: teamB@example.com</li>
            <li>**깃허브 아이디 (레포지토리 링크)**: [teamB-id](https://github.com/teamB-id)</li>
            <li>**자유롭게 소개**: 시스템 통합 및 데이터 처리를 담당합니다.</li>
        </ul>
    </div>
    
    <div style="flex: 1 1 45%; border: 1px solid #e0e0e0; border-radius: 8px; padding: 20px; box-shadow: 2px 2px 5px rgba(0,0,0,0.05);">
        <h3 style="margin-top: 0;"><img src="https://img.shields.io/badge/Pin-E83E8C?style=flat-square&logo=pinboard&logoColor=white" alt="Pin"/> 팀원 C</h3>
        <div style="border: 1px dashed #cccccc; padding: 40px; margin-bottom: 15px; text-align: center; color: #888;">[프로필 이미지 링크]</div>
        <ul style="padding-left: 20px;">
            <li>**담당**: 백엔드 & 핵심 알고리즘 개발</li>
            <li>**이메일**: teamC@example.com</li>
            <li>**깃허브 아이디 (레포지토리 링크)**: [teamC-id](https://github.com/teamC-id)</li>
            <li>**자유롭게 소개**: 딥러닝 모델 최적화 및 테스트를 담당합니다.</li>
        </ul>
    </div>
    
</div>

---

## 🚀 시작하는 방법 (Getting Started)

프로젝트를 로컬 환경에 설정하고 실행하는 방법을 안내합니다.

### 1. 저장소 클론

```bash
# GitHub 저장소 클론
git clone [YOUR_REPOSITORY_URL]
cd [PROJECT_NAME]
