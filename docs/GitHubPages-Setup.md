# Codex Pulse 공개 소개 사이트

웹사이트 소스는 기존 저장소의 `site/`에 있습니다. 앱 설치본·사용자 설정·Google 등록 JSON은 사이트 업로드 대상이 아닙니다. `Publish project website` 워크플로가 `site/`만 배포합니다.

## 최초 한 번: Pages 켜기

1. <https://github.com/BTL-LaserInnovation/CodexPulse/settings/pages>를 엽니다.
2. **Build and deployment → Source**를 **GitHub Actions**로 선택합니다.
3. `Publish project website` 워크플로가 먼저 실패했다면 Actions에서 실패한 작업을 다시 실행합니다. 이후 `site/` 변경은 자동 게시됩니다.

기본 공개 주소(게시 성공 후 사용):

- 홈페이지: <https://btl-laserinnovation.github.io/CodexPulse/>
- 개인정보처리방침: <https://btl-laserinnovation.github.io/CodexPulse/privacy.html>
- 이용약관: <https://btl-laserinnovation.github.io/CodexPulse/terms.html>
- 로고: <https://btl-laserinnovation.github.io/CodexPulse/assets/logo.png>

## Google 소유권 확인

사이트 게시와 Google OAuth 검증 승인은 별개입니다. 무료 `github.io` 주소만으로 OAuth 최종 심사가 통과한다고 보장하지 않습니다.

1. OAuth 프로젝트의 소유자/편집자 Google 계정으로 Google Search Console에 접속합니다.
2. **URL 접두어** 속성에 실제 공개 사이트 주소를 등록합니다.
3. Google이 발급한 HTML 확인 파일 또는 HTML 메타 태그를 사용합니다. 파일명·내용·값을 임의로 만들지 않습니다.
4. HTML 파일 방식이면 받은 파일을 `site/`에 넣어 동일한 파일명으로 공개하고, 메타 태그 방식이면 Google이 준 태그를 `site/index.html`의 `<head>` 안에 넣습니다. 공개 확인 후 Search Console에서 소유권을 확인합니다. 확인 파일/태그는 유지합니다.
5. OAuth의 승인된 도메인과 홈페이지·정책 URL에 대한 Google의 실제 인정 여부를 확인합니다. 하위 경로의 Search Console 확인만으로 상위 도메인의 소유권까지 인정된다고 가정하지 않습니다. 무료 주소가 거절되면 본인 소유 도메인을 연결하는 방법을 검토합니다.

Google 브랜딩 화면의 앱 게시 비활성화 안내를 확인한 뒤 필요한 항목을 채웁니다. 사이트 소유권 확인, 앱 운영 전환, 브랜딩 검증, 캘린더 권한 검증은 각 단계의 실제 상태를 확인해야 합니다.

사이트와 개인정보처리방침은 현재 앱 코드의 동작을 바탕으로 작성했습니다. 향후 Google 권한·저장·전송 방식이 바뀌면 페이지와 앱 내 고지도 함께 갱신해야 합니다. 이 사이트 게시만으로 기존 앱 UI에 정책 링크가 추가되는 것은 아닙니다.
