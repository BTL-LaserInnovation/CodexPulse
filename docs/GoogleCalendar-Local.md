# Google 캘린더 · 로컬 검토판

이 문서는 1.3.0 공개 배포 전 로컬 검토 과정의 이력입니다. 당시 공개 버전은 1.2.2였으며, 로컬 검토 중에는 GitHub Release 게시나 설치된 앱 교체를 수행하지 않았습니다. 현재 설치·업데이트 안내는 프로젝트 루트의 README를 참고하세요.

2026년 11월 11일 최신 로컬 검토판의 주간 달력과 공통 두 행 일정목록은 [현재 로컬 검토 안내](Calendar-Week-Local.md)를 참고하세요. 아래 예시 실행기와 화면 크기는 당시 이력입니다.

## 화면부터 확인하기

프로젝트 루트의 **Run-CalendarPreview.cmd**를 실행합니다.

- 창 제목에 **Codex Pulse · 예시**가 표시됩니다.
- 샘플 Plus 한도와 Google 일정으로 화면을 확인합니다. Google 일정 추가·수정·삭제는 메모리에서만 실행되며 실제 계정에는 연결하지 않습니다.
- 시작 시 주간 63%, 5시간 88%를 보여줍니다. 입력 순서가 5시간→주간이어도 두 화면 모두 주간 민트, 다른 한도 보라색입니다. 잔여 15% 이하의 숫자·막대 경고색은 유지합니다.
- 날짜를 누르면 아래 목록이 바뀝니다. 구글 기간 막대를 누르면 해당 일정이 목록에서 강조됩니다. 목록의 구글 일정을 눌러 편집합니다.
- 창은 360×604 DIP입니다. 기존 580 DIP에서 날짜 행에 필요한 24 DIP만 늘렸습니다. 일정 목록은 기본 세 줄을 보여주며, 일정·리셋권 목록의 제목과 시간 글자는 10 DIP로 맞췄습니다.
- 날짜 행은 30 DIP, 날짜 박스는 위쪽 20 DIP입니다. Codex 표시는 박스 안에 두고, 연속 일정의 2 DIP 선은 박스 바로 아래에 붙입니다. 박스와 선 간격은 1 DIP, 선과 다음 주 박스 간격은 7 DIP이며 선의 클릭 영역은 6 DIP입니다.
- Google 휴일 캘린더의 공휴일 날짜는 일요일과 같은 빨간색입니다. 기념일은 공휴일로 처리하지 않습니다. 휴일 캘린더의 일정 이름은 목록에 남기고 일반 Google 아이콘·기간 막대에서는 제외합니다.
- 일정 편집의 시작·종료 날짜는 달력 아이콘을 눌러 선택할 수 있고 직접 입력도 가능합니다. 팝업은 한글 월·요일과 월/연도 이동을 지원하며, 메인과 같은 색상·글꼴을 사용합니다. 모든 대화상자와 내부 달력·펼침 목록은 항상 불투명하고 메인 위젯의 배경 투명도는 유지합니다.
- Google 일정 목록 오른쪽 휴지통은 편집창을 거치지 않고 제목·실제 날짜/시간을 표시하는 삭제 확인창을 엽니다. 취소하면 요청하지 않습니다. 읽기 전용·공휴일·반복 원본에는 휴지통이 없고, 반복 회차의 휴지통은 해당 회차만 삭제합니다. 전체 반복 삭제는 기존 편집창에서 처리합니다.
- `＋`로 새 일정 작성, 저장·삭제·종일·알림·시간 입력을 확인할 수 있습니다. 예시 데이터는 창을 닫으면 폐기됩니다.
- 이 미리 보기는 Codex 조회·Google 네트워크·Windows 자동실행·공개 업데이트를 실행하지 않습니다.

## 실제 기능이 들어간 로컬 앱

`publish/local-preview/app/CodexPulse.exe`를 직접 실행할 수 있습니다. Google 설정은 EXE 옆에도 포함돼 있어 실행 스크립트 없이 읽습니다. 직접 실행한 앱의 개인 데이터는 기본 위치 `%LOCALAPPDATA%\CodexPulse`에 저장됩니다.

설치된 앱과 개인 데이터를 분리해 검토하려면 프로젝트 루트의 **Run-LocalPreview.cmd**를 실행합니다.

- 실행 파일: `publish/local-preview/app/CodexPulse.exe`
- 데이터: `publish/local-preview/state/`
- 설치된 버전과 데이터·중복 실행 범위를 분리합니다. 실제 Codex 인증은 Codex 자체가 처리하며 위젯은 계정·사용량을 읽습니다.
- 로컬 미설치 앱이므로 Windows 자동실행 설정은 변경하지 않습니다.
- Google은 아래 OAuth 설정이 있어야 실제 연결됩니다. 설정이 없으면 연결 관리 창에서 준비가 필요함을 표시합니다. 구글 앱을 PC에 별도로 설치하는 절차는 없습니다.
- 연결되지 않은 상태에서도 `＋`와 새로 고침 버튼이 연결 절차로 이어집니다. 개발자 등록이 준비되면 연결 버튼은 기본 브라우저의 Google 로그인부터 시작하며, 로그인 후 표시할 캘린더를 고릅니다.
- 개발자 등록 정보가 없는 경우에는 브라우저 로그인을 시작할 수 없습니다. 안내창의 **연결 다시 확인**은 실제 설정을 다시 읽으며, 설정을 만든 뒤 앱을 재시작하지 않아도 연결할 수 있습니다. 이 안내를 실제 로그인 성공으로 간주하지 않습니다.

2026-11-09: 사용자가 제공한 데스크톱 앱용 설정을 로컬 검토판 데이터 폴더에 적용했습니다. 실제 검토판의 설정 로더로 `IsConfigured=true`와 원본 값 일치를 확인했으며, 인증 값은 출력하지 않았습니다. 실제 Google 계정 선택·권한 승인과 Google 서버 일정 동기화는 아직 확인하지 않았습니다. 컴퓨터 제어 도구가 실행 중인 위젯 창을 찾지 못해 연결 버튼의 실제 클릭 검증은 수행하지 못했습니다.

같은 날 13:21 KST 재빌드: Google 설정을 EXE 옆에도 복사하고 직접 실행 경로의 설정 로딩을 검증했습니다. Release x64 빌드는 경고·오류 0, Google UI 검사 95개가 통과했습니다. 사용자 화면에서 Google 인증 페이지가 열린 것을 확인했으며, 현재 차단은 테스트 모드 앱의 허용된 테스트 사용자 제한(`403 access_denied`)입니다. JSON을 발급한 Google Cloud 프로젝트의 Google 인증 플랫폼 → 대상 → 테스트 사용자에 실제 로그인할 계정을 등록한 후 재시도해야 합니다. 실제 로그인 완료·실제 일정 동기화는 아직 검증하지 않았습니다.

같은 날 14:01 KST 수정판: 사용자는 승인 응답 수신까지 진행했지만 계정 창에 연결 실패 원인이 표시되지 않았습니다. 메인 로그인 경로의 오류를 계정 창에 전달하도록 수정했습니다. API 미활성화·권한 부족·요청 제한은 구조화된 오류 사유를 고정 안내문으로 변환합니다. 원본 응답이나 토큰은 출력하지 않습니다. 브라우저의 콜백 안내는 최종 연결 완료가 아닌 수신·후속 확인 단계임을 명시합니다. Debug/Release x64 경고·오류 0, 기본 검사 230개, Google UI 104개, 기본+OAuth 루프백 검사 273개가 통과했습니다. 실제 후속 실패 원인과 실계정 연결 성공은 재시도 확인이 필요합니다.

같은 날 14:39 KST 달력 수정판: 날짜 박스와 기간선 영역 분리, 공휴일 색상 구분, 날짜 선택기를 적용했습니다. 사용자가 제공한 화면에서 Google 연결과 휴일 가져오기를 확인했습니다. 실제 Google 공개 대한민국 휴일 ICS에서 `공휴일`/`기념일` 분류를 확인했으며, 개인 일정이나 인증 정보를 조회한 검증이 아닙니다. 지원하는 명시적 분류가 없는 혼합 휴일은 보수적으로 공휴일 처리하지 않습니다. `.official` 전용 소스의 보조 판별은 소스 규칙에 따른 추론입니다.

14:39 수정판 검증: Debug/Release x64 빌드는 경고·오류 0개, 기본 검사 245개, 검토 파일 Google UI 129개, 기본+가짜 OAuth 루프백 288개, 기존 전체 UI 205개가 통과했습니다. 1x/1.5x에서 박스·선·선택 공휴일·월/연도 팝업을 확인했습니다. 실계정 일정 쓰기·삭제는 자동 검증하지 않았습니다.

같은 날 15:08 KST 수정판: 주간 선 간격 재조정, 항상 불투명한 대화상자, 목록 직접 삭제를 적용했습니다. Debug/Release 경고·오류 0, 최종 검토 파일 Google UI 173개, 기존 전체 UI 205개와 기본+가짜 OAuth 루프백 288개가 통과했습니다. 메인 배경 55%에서 대화상자·날짜 달력·펼침 목록의 실제 렌더링과 불투명도를 확인했습니다. 삭제 성공·취소·실패·ETag/계정 변경·중복 클릭·반복 회차·종료 취소·업데이트 연기는 가짜 일정으로 검증했습니다. 실제 Google 일정은 검증 목적으로 삭제하지 않았습니다.

재현 빌드: 앱을 닫고 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-LocalPreview.ps1`을 실행합니다. 설정 원본은 기본적으로 검토판의 `state/google-oauth-client.json`을 사용하며, EXE 옆에도 복사합니다. 빌드 스크립트는 공개 Release를 만들지 않습니다.

## Google 로그인 설정 · 개발자 준비

일반 사용자는 배포 시 `Google 연결` → 브라우저 계정 선택 → 권한 승인만 하면 됩니다. 이를 위해 배포 담당자는 Google Cloud에 앱을 등록해야 합니다.

1. Google Cloud 프로젝트에서 **Google Calendar API**를 활성화합니다.
2. Google Auth Platform의 앱 이름·동의 화면·대상을 설정합니다. 테스트 상태라면 실제 연결을 시험할 Google 계정을 테스트 사용자로 등록합니다.
3. OAuth 클라이언트를 **데스크톱 앱** 유형으로 생성하고 JSON을 내려받습니다. 웹 앱용 클라이언트와 다릅니다.
4. 파일 이름을 `google-oauth-client.json`으로 바꿔 다음 중 하나에 둡니다.
   - 로컬 검토판: `publish/local-preview/state/google-oauth-client.json`
   - 또는 로컬 실행 파일 옆: `publish/local-preview/app/google-oauth-client.json`
   - 일반 앱 기본 데이터 위치: `%LOCALAPPDATA%\CodexPulse\google-oauth-client.json`
5. 로컬 앱의 **Google 연결** 또는 안내창의 **연결 다시 확인**을 누릅니다. 브라우저에서 계정과 접근 권한을 선택한 다음, 표시할 기존 캘린더를 고릅니다.

환경 변수 `CODEX_PULSE_GOOGLE_CLIENT_ID` 및 선택적 `CODEX_PULSE_GOOGLE_CLIENT_SECRET`로도 개발자 설정을 제공할 수 있습니다. OAuth 설정 파일·인증 정보는 Git 추적에서 제외합니다. 공개 배포 전에는 실제 OAuth 로그인·권한·Google의 앱 공개/검증 요건을 별도로 확인해야 합니다.

요청 권한은 계정 이메일, 캘린더 목록 읽기, 일정 읽기·쓰기에 한정합니다. 비밀번호를 위젯에 입력하지 않습니다. 브라우저 로그인은 PKCE와 임시 loopback 콜백으로 처리합니다.

## 동기화와 편집

- Google에서 변경한 일정은 실행 중 60초마다 변경분을 조회하며, 새로 고침 버튼으로 즉시 조회할 수 있습니다.
- 위젯에서 저장·삭제하면 실제 Calendar API에 전송합니다. 오류나 충돌이면 편집 내용을 보존합니다.
- 캘린더 권한과 일정 수정 권한이 없는 항목은 읽기 전용으로 표시합니다.
- 반복 일정은 개별 항목과 전체 반복을 구분합니다. 전체 선택 시 원본의 시작 날짜와 반복 규칙을 불러옵니다.
- 종일 일정은 시작일과 마지막 날을 입력합니다. Google API의 배타적 종료일 변환은 내부에서 처리합니다.
- 시간 일정은 PC 시간대로 표시합니다. 제목만 수정하면 기존 시각·초·오프셋·반복 시간대를 보존합니다.
- 연결 정보는 데이터 폴더의 `google-auth/credentials.dpapi`에 Windows 사용자별 DPAPI로 보호합니다. Codex 인증 저장소와 분리합니다.
- Google 연결 해제는 이 PC의 연결 정보만 제거하며 원본 Google 일정을 삭제하지 않습니다.

## 로컬 검증

```powershell
dotnet build UsageRemaining.sln -c Debug -p:Platform=x64 -p:UseSharedCompilation=false -m:1
dotnet build UsageRemaining.sln -c Release -p:Platform=x64 -p:UseSharedCompilation=false -m:1
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-ui
./tests/UsageRemaining.Checks/bin/Release/net10.0-windows/UsageRemaining.Checks.exe --google-oauth
```

`--google-oauth`는 실제 외부 계정에 연결하지 않고, 임시 loopback 수신기와 가짜 Google 응답으로 브라우저 콜백·PKCE·취소를 검증합니다. Windows 수신기 접근이 제한된 격리 환경에서는 정상 Windows 사용자 환경에서 실행해야 합니다. 실제 계정 검증과 가짜 HTTP/서비스 검증은 구분합니다. 가짜 서비스 테스트가 통과해도 실제 OAuth 설정과 실제 Google 계정에서의 최종 확인을 대신하지 않습니다.

참고: [Google 데스크톱 OAuth](https://developers.google.com/identity/protocols/oauth2/native-app), [증분 동기화](https://developers.google.com/workspace/calendar/api/guides/sync), [동시 수정 보호](https://developers.google.com/workspace/calendar/api/guides/version-resources).
