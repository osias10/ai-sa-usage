# Runtime provisioning probe

Windows x64에서 공식 실행 파일 자동 준비를 수동으로 검증하는 콘솔 프로젝트입니다. `dotnet test`에는 포함되지 않습니다.

저장소 루트에서 .NET 10 SDK로 실행합니다.

```powershell
# --version / --help만 실행합니다. 로그인·토큰 조회·사용량 조회·모델 실행은 하지 않습니다.
dotnet run --project tests/AIUsage.RuntimeProbe/AIUsage.RuntimeProbe.csproj
```

저장소의 로컬 SDK를 준비했다면 `dotnet` 대신 `.tools/dotnet/dotnet.exe`를 사용합니다.

각 실행은 `.artifacts/runtime-probe-<UTC 시각>-<식별자>`에 Claude·Codex·Antigravity 원본 실행 파일을 새로 다운로드합니다. 공식 출처에서 고정한 체크섬, 두 번째 호출의 캐시 검증, 빈 자식 프로세스 PATH에서의 도움말·버전 실행을 확인합니다. 전체 제한 시간은 15분이며 `Ctrl+C`로 취소할 수 있습니다. 다운로드한 파일은 검증 후에도 해당 폴더에 보관합니다.

인터넷 연결과 약 730 MiB의 여유 공간이 필요합니다. 성공은 종료 코드 `0`, 실패는 `1`, 취소·전체 시간 초과는 `2`입니다. 이 검증은 실제 구독 계정 연결이나 사용량 정확도 검증을 대신하지 않습니다.
