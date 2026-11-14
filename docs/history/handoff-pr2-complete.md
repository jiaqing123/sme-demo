# 交接：PR 2 已完成，下一步 PR 3

> 生成于 2026-11-13 下午（PR 2 提交后，未 push）。仓库 `D:\GitHub\sme-demo`。
> 上一份交接 [`handoff-pr1-complete.md`](handoff-pr1-complete.md) 的 §4 九项范围已全部落地；
> 本文件取代它作为后续工作的起点。PR 1 与 PR 2 都已在本机 `main` 上。

## 1. 当前状态

- 分支：`main`，HEAD `ee9554b`，已 push，与 `origin/main` 同步。
- 工作区：干净。
- 构建：`dotnet build SmeDemo.slnx` 与 `--configuration Release` 都是 **0 warning / 0 error**。
- 测试：**199 passed / 0 failed**（Debug 与 Release 都跑过）
  - SmeDemo.Api.Tests 59
  - SmeDemo.Auth.Tests 64
  - SmeDemo.Application.Tests 25
  - SmeDemo.Infrastructure.Tests 16
  - SmeDemo.Architecture.Tests 8
  - SmeDemo.SmsMock.Tests 15
  - IssuerMock.Tests 8
  - Migrator.Tests 6
- `dotnet format SmeDemo.slnx --verify-no-changes` 通过（本机仍有 `.dcproj` 无害警告）。
- `docker compose config -q` 通过；**完整 Compose 最小栈 + smoke 在本机跑通**：auth `/healthz`、api `/readyz`、login、verify、`/api/v1/me` 200、匿名 401，全部 PASS。
- 两个 EF 模型均 `has-pending-model-changes` 为否；漏洞扫描 16 个项目无已知漏洞。
- `openapi/v1.json` 已随 PR 2 重新生成并提交，Release 构建后无 diff。
- **GitHub Actions 已在远端实际执行并全绿**：run #8（`ee9554b`）的 5 个 job 全部 success
  ——.NET build/tests（含 format、OpenAPI、迁移、漏洞、gitleaks）、Web、Compose validation、
  Image build、Compose smoke。
- **未验证**：Linux 本机以外的 `dotnet format` 已经由 CI 覆盖；Azure Pipelines 仍从未排队。
- gitleaks 本机未安装，未跑；CI 会跑（工作树 + 浅历史）。

## 2. PR 2 的提交

| 提交 | 内容 |
|---|---|
| `df39bad` | 两个服务的统一错误信封：`instance` + `traceId`、405 `METHOD_NOT_ALLOWED`、429/503 `Retry-After`、全环境 `ValidateOnBuild`/`ValidateScopes` |
| `c231ddc` | 测试 host 用 `UseSetting` 每工厂注入配置，移除进程环境变量；两工厂隔离回归测试 |
| `e5ab926` | auth HTTP 测试 host（EF InMemory + 真 seeder + fake SMS + 手动时钟）与 6 个线上契约测试；`AuthOptions` 改为读 `IConfiguration` |
| `d4e4800` | `/readyz`（SQL/Blob/auth metadata，5 秒预算）、Compose healthcheck 与 smoke 改用 readiness |
| `a3f2341` | auth 按 IP + 按账号限流，API 按用户 + 按端点限流；429 + `Retry-After`；转发头显式开关 |
| `17e7c68` | metadata/JWKS 网络故障映射 503；API/auth `nosniff`；art 的 CSP `default-src 'none'; sandbox` |
| `af2a786` | 两个服务的状态码矩阵测试（400/401/403/404/405/429/500/503） |
| `fd297bd` | 文档：requirements/threat-model/development/README/ADR-017，删除过期声明 |
| `fb3b6fb` | 独立审查后的阻塞修复：413/415 保留原状态码并进入统一信封；补 verify/resend 按 IP 与转发头分区测试 |
| `ee9554b` | 独立审查后的文档纠偏：413/415、metadata 401 限定、demo 默认值、nginx “唯一入口”措辞、2026-11-13 smoke 记录 |

## 3. PR 2 九项范围的落点

1. **错误信封**：`Errors/ProblemResponseWriter` + `ProblemResults` 统一写 `status/title/type/detail/instance/traceId/error.code/message`；405 在 fallback policy 之前识别，不再变 401。
2. **状态码矩阵**：`tests/SmeDemo.Api.Tests/StatusCodeMatrixTests.cs`、`tests/SmeDemo.Auth.Tests/AuthStatusCodeMatrixTests.cs`，断言 `application/problem+json`、`error.code`、`traceId`、`instance`、`nosniff`、`Retry-After`、无实现细节。
3. **限流**：
   - auth：login/verify/resend 按 IP，login 再按规范化用户名；默认 login 20/IP/min、10/账号/5min、verify 60/IP/min、resend 10/IP/min；配置键 `AUTH_RATE_LIMIT_*`。
   - API：`/api/v1` 每用户每端点 120/min，键 `API_RATE_LIMIT_PERMIT`/`API_RATE_LIMIT_WINDOW`；限流在认证/授权之后，匿名仍是 401。
   - 全部固定窗口、内存、单进程。
4. **`/readyz`**：DB、Blob、auth metadata 并行检查，5 秒预算；200 ready / 503 degraded + `Retry-After: 5`；`/healthz` 仍是纯 liveness。Compose api healthcheck 与 smoke 已切换。
5. **auth HTTP 集成测试**：`AuthTestFactory`（EF InMemory、唯一 key 目录、RecordingSmsProvider、ManualTimeProvider）+ 未知用户/错密码不可区分、OTP 三次销毁、resend cooldown、SMS 503、400/404/405 信封。
6. **全环境 DI 验证**：两个 `Program.cs` 都设置 `ValidateOnBuild = true`、`ValidateScopes = true`。
7. **`ApiTestFactory` 配置注入**：`UseSetting` 替代 `Environment.SetEnvironmentVariable`；`ArchitectureApiFactory` 同步；有跨工厂隔离测试。
8. **安全头**：所有 API/auth 响应 `X-Content-Type-Options: nosniff`；art 成功响应加 CSP sandbox，保留 SVG content type 与私有缓存。
9. **JWT 上游故障**：metadata/JWKS 拉取传输失败 → 503 `UPSTREAM_UNAVAILABLE` + `Retry-After: 5`；签名/iss/aud/exp/alg 失败仍 401。最多 2 秒的二次确认避免黑洞地址拖住请求。

补充（独立审查发现并已修）：`BadHttpRequestException` 现在保留框架给的 413/415 状态，新增
`PAYLOAD_TOO_LARGE` / `UNSUPPORTED_MEDIA_TYPE`，两个服务的 status-code pages 也映射这两个状态，
所以超限/不支持的 body 不再逃出统一信封；auth 补了 verify/resend 的按 IP 预算与
`X-Forwarded-For` 分区测试。

## 4. PR 2 期间新增的设计决定（详见 ADR-017）

- 限流与 readiness 都按实例设计；多副本需要共享限流器或按副本数缩放预算，文档已写为 demo 限制。
- `AUTH_TRUST_FORWARDED_HEADERS` 默认 false；Compose 因 nginx 是唯一入口而设 true，并清除默认已知代理；生产必须改为精确代理/网段且不能直暴 auth。demo 仍发布 `127.0.0.1:8081`，本机可伪造 `X-Forwarded-For`。
- 挑战三次错误后的 429 也带 `Retry-After`（1 秒，仅防止热重试；用户可立即重新登录）。
- metadata 有缓存：缓存过期/invalid 时仍可能先出现 401 `INVALID_TOKEN`，直到刷新；这是 JwtBearer 的固有行为，已在文档与排障中写明。
- 限流配置非法（非正整数/非法时长）会让容器启动失败，不静默关闭控制。
- `AuthOptions` 已从 `FromEnvironment` 改为 `FromConfiguration`（环境变量仍是配置源），PR 3 的对应项提前完成。

## 5. PR 3：交付与可观测性（按原计划，去掉已完成项）

- 基础镜像按 digest 固定、Dependabot、Trivy/镜像扫描、SBOM、`global.json`。
- Compose 加固：`read_only`、`cap_drop: [ALL]`、`no-new-privileges`、资源限制；请求超时 / body-size 限制（当前只有 nginx 1 MB）。
- OpenTelemetry（AspNetCore/Http/EF/Storage）+ JSON console + `X-Request-Id`；当前只有 `traceId`，没有集中遥测。
- `art_url` 去掉 `GetCardsQuery.cs` 里 `/api/v1` 硬编码，改用 `LinkGenerator`（`docs/reference/versioning.md` 已记录为 P1）。
- runbook、版本/弃用策略落地（`docs/reference/versioning.md` 已定规则，未落实现）。
- 容器加固（DHI / chiseled）作为独立工作项，见 `docs/azure-pipelines.md` §Container image hardening——不能只换 tag，healthcheck 依赖 curl/wget 是主要阻碍。
- 分布式限流（Redis 等）与元数据缓存失效策略，作为生产化前置项；ADR-017 已记录。
- `/readyz` 的加固：目前匿名、不计量，每次探测都做 DB/Blob/metadata 三次下游调用；
  auth metadata 只校验“200 + JSON 对象”，没有校验 issuer/jwks_uri。若探针会暴露到
  编排器之外，需要加缓存/节流并收紧校验。测试里 probe 是 fake，真实 probe 行为没有集成覆盖。
- OpenAPI 的 `ProblemDetails` schema 仍只描述 `type/title/status/detail/instance`，没有
  `traceId`/`error` 和 `Retry-After` header；生成客户端目前看不到完整错误契约。
- 请求体上限仍用 Kestrel 默认值（nginx 边缘 1 MB）；如需显式控制，加
  `MAX_REQUEST_BODY_BYTES` 并写测试（TestServer 不强制 Kestrel 的 body limit，
  413 目前只有 handler 级测试）。

## 6. Azure 交付线：仍需要你确认的 5 个决策

`azure-pipelines.yml` 的 Build 阶段已完整，部署阶段默认关闭且故意失败，直到真实迁移/发布步骤补上。开工前需要定：

1. 托管方式：AKS / App Service / Container Apps（决定 rollout 任务）。
2. 代码源：继续 GitHub（Azure Pipelines 连 GitHub）还是迁 Azure Repos。
3. Test 是否每次 main 自动部署；UAT 自动还是手动；Prod 是否只靠 Environment 审批，还是还要变更单/时间窗。
4. ACR 命名、service connection 命名、变量组命名。
5. GitHub Actions 的去留：保留 PR 快速反馈，还是稳定后收敛到 Azure Pipelines。

## 7. 仍然开放的技术决策

1. 契约是否长期留在 `Application`，还是等第二个消费方出现再抽 `SmeDemo.Contracts`（审计建议后者）。
2. 安全基线取舍：PCI 范围（确认不引入 PAN/CVV/PIN）、MFA/step-up、WAF/SIEM、HSM/KMS、数据保留、欺诈监控。
3. gitleaks 目前只扫工作树（`--no-git`，CI 是浅 checkout）；建议定期对完整 git 历史单独扫一次，或在上线前做一次历史审计。
4. `openapi/v1.json` 没有 `servers` 字段（运行时由请求推导，契约测试里做了归一化）。如果客户端需要固定 base URL，可以在生成时注入 `PUBLIC_BASE_URL`。
5. 文档避免写会随代码变化的计数；PR 2 文档已按此规则清理（smoke 不再写固定条数）。

## 8. 收尾事项（全部工作完成后再做）

- 把 `progress/` 里**长期有效**的内容整理成英文后放入 `docs/`（面向领导和同事），候选：基线状态摘要、PR 3 的范围与验收标准、Azure 交付线决策、以及仍然开放的技术决策。`progress/` 继续只留本机中文流水账和排障细节。
- 现在不做：`docs/` 是对外英文材料，不混入中文工作笔记；等 PR 3 落定后一次性整理，避免写两遍。

## 9. 恢复步骤

```powershell
git status -sb
git log -3 --oneline
dotnet build SmeDemo.slnx --nologo
dotnet test SmeDemo.slnx --nologo -v q
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
```

预期：HEAD `ee9554b`（或其后提交），工作区干净，199 测试全绿。

需要跑完整 smoke 时：

```powershell
Copy-Item .env.example .env -Force   # 仅当 .env 不存在
docker compose up -d --build mssql azurite db-migrator db-migrator-auth db-grants auth api
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

本机 2026-11-13 已在最终 HEAD `ee9554b` 验证：7 个 smoke 检查全部 PASS（包含 api `/readyz`）。

## 10. 本机 clone 的关系

- `D:\GitHub\sme-demo`：主仓库，`origin` = `https://github.com/jiaqing123/sme-demo.git`。
- `D:\GitHub\sme-demo-ai`：实验 clone，分支 `experiment/ai-search`，`upstream` 指回主仓库（push 已禁用）。后续两边继续用 `git fetch upstream` + merge 同步，不要互相 push。

## 11. 工作方式约定（沿用）

- 主代理负责规划、拆分任务、复核关键 diff、最终提交。
- 大规模代码检查/实现交给子代理，避免主对话上下文被细节灌满。
- 每个 PR 至少：实现子代理自测 → 独立验证子代理审查 + 复跑 → 主代理复跑并提交。
- 提交前检查 secrets、依赖、`bin/obj`、`git diff --check`。
- 不追“已知差异”，不引入 MediatR/AutoMapper/MVC/K8s；保持演示项目可在普通笔记本运行。
