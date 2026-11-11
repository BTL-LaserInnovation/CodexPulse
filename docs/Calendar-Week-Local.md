# 달력 주간 접기 · 로컬 검토판

2026년 11월 10일 작업. 공개 버전 번호는 변경하지 않았으며 GitHub 업로드, Release 게시, 자동 업데이트 배포를 수행하지 않았습니다.

2026년 11월 11일 일정 영역을 추가 정리했습니다. 아래 크기와 검증 결과는 이 최신 로컬 검토판 기준입니다.

## 실행

프로젝트 루트의 `Run-LocalPreview.cmd`를 실행합니다. `publish/local-preview/app/CodexPulse.exe`를 열되, 설치된 앱과 구분되는 `publish/local-preview/state/`에 설정을 저장합니다. EXE 옆에 기존 Google 데스크톱 등록 설정도 복사했습니다. 등록 값이나 개인 로그인 토큰은 문서에 포함하지 않습니다.

EXE를 직접 실행하면 기본 데이터 위치와 기존 설치 앱의 단일 실행 범위를 사용할 수 있으므로, 이번 결과를 검토할 때는 위 실행 스크립트를 권장합니다.

## 동작

- 앱 제목의 전체 접기·펼치기는 기존 그대로입니다.
- 달력 제목 왼쪽의 작은 화살표로 월 전체와 한 주를 전환합니다.
- 주간 접기에 진입하면 PC 현지 시간의 오늘을 선택하고, 오늘을 포함한 일요일~토요일 7일을 표시합니다. 예를 들어 2026-11-19는 11-15~11-21입니다.
- 주간 상태의 이전·다음 버튼은 7일씩 이동합니다. 월간 상태에서는 한 달씩 이동합니다. 오늘 버튼은 현재 날짜로 돌아옵니다.
- 월간 달력은 42일, 주간 달력은 7일입니다. 날짜 줄의 높이와 일정 막대 영역은 유지합니다.
- 날짜 선택과 일정 목록, 공개·개인 리셋 표시, 공휴일 색상, Google 일정, 리셋권 목록은 유지합니다. 연속 일정 막대는 표시된 주에 맞춰 자릅니다.
- 주간에서 다른 달에 속한 날짜를 선택해도 표시 중인 주가 바뀌지 않습니다. 월간으로 펼치면 선택 날짜가 속한 달을 보여줍니다. 이전·다음 이동만으로 선택 날짜가 바뀌지는 않습니다.
- 오늘을 따라 보고 있으면 자정에 날짜가 갱신됩니다. 과거·미래 주를 직접 보고 있을 때는 임의로 오늘로 되돌리지 않습니다.
- `IsCalendarCollapsed` 설정은 앱 전체의 `IsCollapsed`와 독립적으로 저장합니다. 기존 설정 파일에는 새 값이 없으므로 처음에는 기존 월간 화면입니다.
- 월간·주간 모두 일정 제목 줄은 날짜·현지 시간 → 새로고침 → 일정 추가 → 오른쪽 끝의 기존 색상 범례 순서입니다. 동기화 시각·상태·오류 세부 정보는 새로고침 툴팁과 접근성 도움말로 제공합니다. 동기화 중 비활성 버튼에도 툴팁을 표시하고, 오류·경고는 버튼을 산호색으로 표시합니다.
- Codex 기록과 Google 일정이 함께 사용하는 목록은 두 행(48 DIP) 높이로 고정합니다. 초과하면 내부 스크롤을 사용하며, 항목이 0~1개여도 창 높이는 바뀌지 않습니다. 기존 글씨 크기와 일정 편집·삭제 동작을 유지합니다.
- 일정 영역을 110→78 DIP로 줄여 월간·주간 창 모두 이전 검토판보다 32 DIP 짧습니다. 일정목록과 리셋권 제목 사이에는 월간·주간 동일하게 8 DIP 여백을 둡니다. 현재 펼친 창은 월간 360×572 DIP, 주간 360×422 DIP입니다. 전체 접기 상태의 360×84/112 DIP는 그대로입니다.

## 검증

- Debug 및 Release x64 빌드: 경고 0, 오류 0.
- 기본 검사 282개, 기존 Google UI 검사 173개, 주간 달력 검사 47개, 공통 일정 배치 검사 41개 통과. 일정 배치와 기존 Google UI 검사는 각각 독립된 프로세스에서 실행합니다.
- 공통 일정 검사는 월간·주간 각각 0/1/2/3개 항목의 한 줄 헤더 배치, 범례 오른쪽 정렬, 고정 두 행, 내부 스크롤, 동기화 중 툴팁, 오류·경고 표시와 재시도 후 복구를 확인했습니다.
- 주간 검사는 현재 PC 날짜, 일요일 기준, 월·연도 경계와 윤년, 150 DIP 높이 감소, 독립 설정 저장·재실행, 오늘·날짜 선택·이전/다음, 공휴일·4종 Codex 표시, 일정 막대의 주간 자르기, 조회 중 주 이동 시 마지막 범위 재조회, 자정 갱신을 확인했습니다.
- 1.0×/1.5× WPF 렌더링과 독립 QC 소스·화면 검토를 수행했습니다.
- Google 일정 검사는 가짜 서비스와 분리된 테스트 설정을 사용했습니다. 실제 개인 일정을 쓰거나 삭제하지 않았고, Windows 시스템 시계·시간대도 변경하지 않았습니다.

검증 명령:

```powershell
dotnet build UsageRemaining.sln -c Debug -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --no-restore
dotnet build UsageRemaining.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --no-restore
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --calendar-week-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --agenda-ui
```
