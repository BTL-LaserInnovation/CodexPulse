# Google 로그인 취소 및 한도 카드 축소 · 로컬 검토판

이 문서는 1.3.3 공개배포 전 로컬 검토 기록입니다. 아래 미배포 설명은 당시 검토 시점 기준이며 현재 설치·업데이트 안내는 프로젝트 루트의 README를 참고하세요.

2026년 11월 11일 로컬 변경입니다. 공개 버전 1.3.2의 설치 파일과 자동 업데이트 피드는 변경하지 않았습니다.

프로젝트 루트의 `Run-LocalPreview.cmd`로 실행합니다. 설치형 앱과 분리된 `publish/local-preview/state` 설정을 사용하며, 실행 파일은 `publish/local-preview/app/CodexPulse.exe`입니다.

## 동작

- Google 로그인 대기 중 기존 연결 버튼은 활성화된 `연결 취소`로 바뀝니다. 취소 시 수신기·저장 정리가 끝나면 `Google 연결`로 복원되어 재시도할 수 있습니다.
- 로그인 대기 제한은 5분입니다. 시간 초과 시 재시도 가능한 상태와 툴팁 안내로 돌아갑니다. 사용자 취소나 시간 초과만으로 오류 대화상자를 띄우지 않습니다.
- 취소된 로그인 요청의 오래된 응답은 새 연결에 적용하지 않습니다. 인증이 완료되면 로그인 타이머와 취소 동작을 끝내며, 이후 캘린더 조회·관리 중에는 로그인 시간 초과로 처리하지 않습니다.
- 기존 Google 계정 변경 대화상자는 닫기·중단을 통해 취소할 수 있습니다. 인증 실패나 취소 시 기존 연결 정보는 유지합니다.
- 앱 종료는 진행 중인 로그인 취소와 저장 복구를 기다린 후 서비스를 정리합니다.
- 팝업 배경에 사용하는 고정 브러시는 바인딩이 직접 유지하도록 보완했습니다. 기존 배경 투명도 계산과 글자·컨트롤의 불투명도는 같습니다.
- 펼친 양쪽 한도 카드는 남은 시간 줄을 없애고 `MM-dd HH:mm 리셋` 한 줄만 표시합니다. 전체 날짜·초·시간대는 기존 툴팁에서 확인합니다. 시각이 없는 경우 안내도 한 줄입니다.
- 카드 높이는 90→76 DIP, 창은 월간 360×558 / 주간 360×408 DIP입니다. 글꼴·색상·진행 막대, 두 행 일정목록과 리셋권 위 8 DIP 여백은 유지합니다. 전체 접기 상태의 한도 정보는 기존과 같습니다.

## 검증 방법

최종 Debug·Release x64 빌드는 경고 0·오류 0입니다. 로그인 UI 검사 23개(Debug/Release), 실제 로컬 콜백을 사용하는 Google/OAuth 검사 347개, 기존 Google UI 173개, 일정 배치 41개, 주간 달력 47개, 가짜 공급자를 사용하는 전체 UI 181개를 통과했습니다. 전체 UI 검사는 네이티브 트레이 검사를 제외했으며 1.0×/1.5× 렌더링과 독립 QC를 확인했습니다. 게시용 버전 변경이나 GitHub 배포는 하지 않았습니다.

`--google-connect-ui`는 가짜 Google 서비스로 대기→취소→정리 중 재시도 차단→재연결, 시간 초과, 지연된 응답, 인증 후 관리, 앱 종료를 검사합니다. `--google-oauth`는 실제 로컬 콜백 수신기와 가짜 HTTP/자격증명 저장소로 취소·재시도·오래된 응답·저장 경계를 확인합니다. 실제 Google 계정이나 일정은 검증 목적으로 변경하지 않습니다.

```powershell
dotnet build UsageRemaining.sln -c Debug -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --no-restore
dotnet build UsageRemaining.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1 --no-restore
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-connect-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-oauth
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --agenda-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --calendar-week-ui
```
