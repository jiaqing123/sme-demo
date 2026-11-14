# 交接：PR 3 已完成（交付与可观测性）

> 生成于 2026-11-14 晚（PR 3 提交并 push 后）。仓库 `D:\GitHub\sme-demo`。
> 上一份交接 `handoff-pr2-complete.md` 的 §5 范围里，与运行时可观测性、供应链、
> Compose 加固、art_url、OpenAPI 错误契约相关的项已完成；DHI/chiseled、分布式
> 限流、版本/弃用机制、Azure 交付决策仍未做。

## 1. 当前状态

- 分支：`main`，HEAD `bd33c08`，已 push，与 `origin/main` 同步。
- 工作区：干净（`progress/` 忽略）。
- 构建：`dotnet build SmeDemo.slnx` 与 `--configuration Release` 均 **0 warning / 0 error**。
- 测试：**234 passed / 0 failed**（Debug 与 Release 都跑过）
  - SmeDemo.Api.Tests 82
  - SmeDemo.Auth.Tests 74
  - SmeDemo.Application.Tests 25
  - SmeDemo.Infrastructure.Tests 16
  - SmeDemo.SmsMock.Tests 15
  - SmeDemo.Architecture.Tests 8
  - IssuerMock.Tests 8
  - Migrator.Tests 6
- `dotnet format SmeDemo.slnx --verify-no-changes` 通过；`docker compose config -q` 通过；
  `python tools/smoke/check-vulnerabilities.py` 16 个项目 PASS。
- **GitHub Actions run `43560262538`（`bd33c08`，当前 HEAD）5 个 job 全部 success**：
  .NET build/tests、Web、Compose validation、Build and scan container images
  （含 6 份 SBOM + Trivy 扫描）、Compose smoke test。
  - 上一个 run `43553676907`（`eb96829`，PR 3 主提交）也全绿；`aa026c0` 的 run 被
    concurrency 取消（正常）。
- Azure Pipelines 仍未排队；其 Images job 的 Trivy/SBOM 步骤只做了 YAML 级校验。
- Dependabot 的 7 个 PR 已处理完，当前 0 个 open PR；见 §7。

## 2. 三个提交

| 提交 | 内容 |
|---|---|
| `bb05709` | `global.json`、Dependabot、全部基础镜像 tag+digest、CI/Azure 的 Trivy+SBOM、Compose 加固、web 换官方 nginx 1.31 非 root + `/tmp/config.js` |
| `23eacb8` | OpenTelemetry（traces/metrics）、JSON console、`X-Request-Id`、Kestrel `MAX_REQUEST_BODY_BYTES`、readiness 2s 缓存与 metadata 校验、art_url LinkGenerator、OpenAPI 错误契约、234 测试 |
| `eb96829` | `docs/observability.md`、`docs/runbook.md`、ADR-018、development/azure-pipelines/requirements/DoD/README/索引更新 |

PR 3 之后的两个提交（都在 `main` 上）：

| 提交 | 内容 |
|---|---|
| `aa026c0` | Dependabot 策略：Docker major 全部 ignore，文档/ADR 说明 major 运行时升级是单独工作项 |
| `bd33c08` | 合并 Dependabot #7：checkout v7、setup-dotnet v6、setup-node v7、upload-artifact v7 |

## 3. 关键实现位置

- 可观测性：`src/SmeDemo.Api/Observability/Telemetry.cs` 与 Auth 同名镜像文件；
  `src/SmeDemo.Api/Http/RequestIdMiddleware.cs` 与 Auth 同名文件。
- 请求上限：`src/SmeDemo.Api/Configuration/RequestBodyLimit.cs` 与 Auth 同名文件；
  两个 `Program.cs` 里 `ConfigureKestrel`。
- readiness：`src/SmeDemo.Api/Readiness/ReadinessService.cs`（单例、2s、single-flight、
  每次刷新新建 scope）、`AuthMetadataReadinessProbe.cs`。
- 卡面 URL：`src/SmeDemo.Application/Abstractions/ICardArtUrlProvider.cs`、
  `src/SmeDemo.Api/Cards/CardArtUrlProvider.cs`、`ApiRoutes.CardArt`。
- OpenAPI：`src/SmeDemo.Api/OpenApi/ProblemDetailsContractTransformer.cs`，重新生成的
  `openapi/v1.json`。
- 操作文档：`docs/observability.md`、`docs/runbook.md`、
  `docs/reference/adr/ADR-018-observability-and-supply-chain.md`。

## 4. PR 3 验证记录（2026-11-14）

- 完整加固栈 `docker compose up -d`（含 web）：
  - smoke 6 项 PASS（auth liveness、api readiness、login、verify、`/api/v1/me`、匿名 401）。
  - 通过 `http://localhost:8080` 走 nginx 完成 login/verify/me/cards；art 返回 200
    `image/svg+xml` + sandbox CSP；`/config.js` 返回 `Cache-Control: no-store`。
  - `X-Request-Id: demo-check-1` 被回显；api/auth 日志是一行一个 JSON，含
    `request_id`、`TraceId`、`SpanId`。
  - `docker inspect`：`ReadonlyRootfs=true`、`CapDrop=[ALL]`、`no-new-privileges:true`、
    `/tmp` tmpfs；`touch /nope` 失败、`touch /tmp/ok` 成功。
  - 2 MB 登录 body 在真实端口返回 413 `application/problem+json`。
- 6 个镜像用与 CI 相同的 Trivy 参数扫描全部干净（web 镜像删掉 xslt 模块后从
  13 个 HIGH/CRITICAL 降到 0）。
- 失败/踩坑记录见 `progress/2026-11-14.md` 与 `RESUME.md` §已知坑。

## 5. 仍然未做（PR 3 原清单中剩下的）

1. **容器加固后续**：DHI/chiseled 基础镜像、从 runtime 去掉 shell/curl、外部探针；
   见 `docs/azure-pipelines.md#container-image-hardening`。当前镜像仍带 shell 和 curl。
2. **分布式限流**（Redis 等）与 metadata 缓存失效策略；ADR-017 已记录为生产化前置项。
3. **`/readyz` 的进一步加固**：现在匿名、无限流，只有 2s 缓存；若要暴露到编排器之外，
   需要限流/更细的校验。真实 probe 的集成覆盖仍缺（测试用 fake probe）。
4. **版本/弃用策略落地**：`docs/reference/versioning.md` 有规则，但没有
   `Deprecation`/`Sunset` header 机制或示例。
5. **OpenAPI `servers` 字段**：仍由运行时推导；客户端需要固定 base URL 时可注入
   `PUBLIC_BASE_URL`。
6. **gitleaks 完整历史**：CI 只扫工作树（浅 checkout）；上线前建议单独扫完整历史。
7. **契约层归属**：继续留在 `Application` 还是抽 `SmeDemo.Contracts`，仍未决定。
8. **Azure 交付线**：`azure-pipelines.yml` 的部署阶段仍是故意失败的占位；开工前需要
   §6 的 5 个决策 + 实际迁移/发布步骤。
9. **Hosted singleton 捕获 transient `HttpClient`**：仍存在（原计划 PR 3 处理，实际未动）。

## 6. Azure 交付线：仍需要确认的 5 个决策（未变）

1. 托管方式：AKS / App Service / Container Apps。
2. 代码源：继续 GitHub（Azure Pipelines 连 GitHub）还是迁 Azure Repos。
3. Test 是否每次 main 自动部署；UAT 自动还是手动；Prod 是否只靠 Environment 审批。
4. ACR 命名、service connection 命名、变量组命名。
5. GitHub Actions 的去留：保留 PR 快速反馈，还是稳定后收敛到 Azure Pipelines。

## 7. Dependabot 处理结果（2026-11-14）

- 首次 push 后开了 7 个 PR，已全部处理，当前 **0 个 open PR**。
  - #1–#5：分别把 `dotnet/sdk`、`dotnet/aspnet`、`dotnet/runtime` 从 **10.0 提到 11.0**，
    与 `net10.0` / `global.json` 不兼容；已带说明关闭（其中 #1–#4 的 Compose smoke 本来
    就是红的，#2 的镜像扫描也失败）。
  - #6：`node` 22 → 25（奇数非 LTS major），已关闭。
  - #7：actions 组升级（checkout v7、setup-dotnet v6、setup-node v7、
    upload-artifact v7）→ 以 squash 合并（`bd33c08`）。该 PR 自身的 5 个 CI job 全绿，
    等于在合并前跑过新 workflow。
- `.github/dependabot.yml` 已对所有 Docker 目录加
  `ignore: dependency-name "*" / version-update:semver-major`：同一 major 内的
  patch/digest 更新仍每周提，major 运行时升级（.NET 11、Node 25、nginx 2.x）改为
  单独工作项。actions 的 major 更新保留，因为 PR 本身会跑升级后的 workflow。
- 注意：Dependabot 的 ignore 对 security update 也生效。若某个基础镜像的安全修复
  只能靠 major 升级，Dependabot 不会提，需要人工跟进；Trivy 扫描仍会暴露问题。
- 关闭的 6 个分支已删除；merge/close 使用的是本机 git credential manager 里的 GitHub
  凭据，未写入任何文件。


## 8. 恢复步骤

```powershell
git status -sb
git log -3 --oneline
dotnet build SmeDemo.slnx --nologo
dotnet test SmeDemo.slnx --nologo -v q
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
```

预期：HEAD `eb96829`（或其后提交），工作区干净，234 测试全绿。

完整栈：

```powershell
docker compose up -d --build
Invoke-RestMethod http://localhost:8081/healthz
Invoke-WebRequest -SkipHttpErrorCheck http://localhost:8082/readyz
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

需要复跑镜像扫描时，用 `progress/RESUME.md` 里的 Trivy 缓存卷命令（默认 5 分钟会超时）。

## 9. 工作方式约定（沿用）

- 主代理负责规划、拆分任务、复核关键 diff、最终提交。
- 大规模代码检查/实现交给子代理，避免主对话上下文被细节灌满。
- 每个 PR 至少：实现子代理自测 → 独立验证子代理审查 + 复跑 → 主代理复跑并提交。
- 提交前检查 secrets、依赖、`bin/obj`、`git diff --check`。
- 不追“已知差异”，不引入 MediatR/AutoMapper/MVC/K8s；保持演示项目可在普通笔记本运行。
