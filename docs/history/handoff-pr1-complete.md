# 交接：PR 1 已完成，下一步 PR 2

> 生成于 2026-11-11 晚（PR 1 提交后）。仓库 `D:\GitHub\sme-demo`。
> 上一份交接 `progress/archive/handoff-reference-baseline.md` 描述的是 PR 1 之前的状态，
> 其中 §6 的范围已全部落地；本文件取代它作为后续工作的起点。

## 1. 当前状态

- 分支：`main`，HEAD `a49cbfd`，比 `origin/main` 领先 **8 个提交**（**未 push**）。
- 工作区：干净。
- 构建：`dotnet build SmeDemo.slnx` **0 warning / 0 error**（Debug 与 Release 都验证过）。
- 测试：**143 passed / 0 failed**
  - SmeDemo.Api.Tests 29（原 20 + OpenAPI 2 + 安全滥用 7）
  - SmeDemo.Architecture.Tests 8（新增）
  - SmeDemo.Auth.Tests 36
  - SmeDemo.Application.Tests 25
  - SmeDemo.Infrastructure.Tests 16
  - SmeDemo.SmsMock.Tests 15
  - IssuerMock.Tests 8
  - Migrator.Tests 6
- `dotnet format SmeDemo.slnx --verify-no-changes` 通过（本机有 `.dcproj` 无害警告）。
- `docker compose config -q` 通过；**完整 compose 最小栈 + smoke 已在本机跑通 6/6**。
- 漏洞扫描：16 个项目无已知漏洞；gitleaks：无发现。
- `openapi/v1.json` 已提交，删除后强制重建与提交版本 **SHA256 完全一致**。
- **GitHub Actions 与 Azure Pipelines 都未在远端实际执行过**。

## 2. PR 1 的提交

| 提交 | 内容 |
|---|---|
| `d896839` | OpenAPI 产物化 + Bearer securityScheme + 2 个契约测试 |
| `0e89870` | 架构测试项目（分层方向、端点纯净度、端点元数据、auth 显式匿名） |
| `f3f83cc` | format/OpenAPI/漏洞/密钥门禁 + `images` + `compose-smoke` + smoke 脚本 |
| `6998548` | 7 个安全滥用用例 + 测试工厂的日志捕获与负向 token 构造 |
| `9739cca` | `docs/reference/` + 修正 requirements/development/README 的过期声明 |
| `a49cbfd` | Azure Pipelines 骨架（Build 门禁 + Test/UAT/Prod 占位）+ 文档 + ADR-016 |

## 3. 唯一还没有被验证的环节

1. **push 后首次 GitHub Actions 运行**。`dotnet` job 超时设的是 20 分钟，新增了
   format（约 36s）、漏洞扫描（约 77s）、gitleaks（约 2s）；冷 runner 上要观察是否够。
2. **Linux 上的 `dotnet format`**：本机 Windows 通过，Linux 只做了文档推断。
3. **Azure Pipelines 未排队运行**：先按 `docs/azure-pipelines.md` §Required Azure
   DevOps configuration 配好环境、service connection、变量组，再跑一次 Build。

## 4. 明天可以直接开工的 PR 2：运行时契约与 auth 加固

范围来自 `docs/requirements.md`、`docs/threat-model.md` §5 和 PR 1 期间发现的缺口：

1. **错误信封补全**：`ProblemDetails` 增加 `traceId`（`HttpContext.TraceIdentifier`）
   与 `instance`；405 现在被 fallback policy 变成 401，需要把 405 映射成统一信封。
2. **状态码矩阵测试**：400/401/403/404/405/429/500/503，断言 `application/problem+json`
   与 `error.code`。
3. **限流**：auth 按 IP + 按账号，API 按端点；`429` + `Retry-After`。威胁模型 §5.1/§5.2。
4. **`/readyz`**：DB/Blob/auth metadata 依赖检查；Compose healthcheck 从 `/healthz`
   改探 `/readyz`；保留 `/healthz` 作为 liveness。
5. **auth HTTP 级集成测试**（PR 1 明确留给 PR 2）：
   - 需要一个第二 host fixture：EF InMemory + 种子用户 + fake `ISmsProvider` + 临时 key store，
     加 `Microsoft.AspNetCore.Mvc.Testing` 引用；
   - 断言未知用户与错误密码响应一致（不枚举，含时序）、3 次错 OTP 销毁 challenge、
     resend cooldown、SMS 故障 503 的线上契约。
6. **全环境** `ValidateOnBuild` / `ValidateScopes`（现在只有 Development）。
7. **`ApiTestFactory` 用进程级环境变量**（`Environment.SetEnvironmentVariable`）：
   改为 `WebApplicationFactory` 的配置注入，避免测试间串扰。
8. **API 安全头**：`X-Content-Type-Options: nosniff`；art（SVG）加 CSP sandbox。
9. **JWT metadata/JWKS 网络故障映射为 503**，不要伪装成 401 `INVALID_TOKEN`。

## 5. PR 3：交付与可观测性

- 基础镜像按 digest 固定、Dependabot、Trivy/镜像扫描、SBOM、`global.json`。
- Compose 加固：`read_only`、`cap_drop: [ALL]`、`no-new-privileges`、资源限制。
- OpenTelemetry（AspNetCore/Http/EF/Storage）+ JSON console + `X-Request-Id`。
- `AuthOptions` 走 `IConfiguration`（现在直接读环境变量）；`art_url` 去掉
  `GetCardsQuery.cs` 里的 `/api/v1` 硬编码，改用 `LinkGenerator`。
- runbook、版本/弃用策略（`docs/reference/versioning.md` 已定规则，未落实现）。
- 容器加固（DHI / chiseled）作为独立工作项，见 `docs/azure-pipelines.md` §Container
  image hardening——不能只换 tag，healthcheck 依赖 curl/wget 是主要阻碍。

## 6. Azure 交付线：需要你确认的 5 个决策

`azure-pipelines.yml` 的 Build 阶段已完整，部署阶段默认关闭且**故意失败**，直到真实
迁移/发布步骤补上。开工前需要定：

1. 托管方式：AKS / App Service / Container Apps（决定 rollout 任务）。
2. 代码源：继续 GitHub（Azure Pipelines 连 GitHub）还是迁 Azure Repos。
3. Test 是否每次 main 自动部署；UAT 自动还是手动；Prod 是否只靠 Environment 审批，
   还是还要变更单/时间窗。
4. ACR 命名、service connection 命名、变量组命名。
5. GitHub Actions 的去留：保留 PR 快速反馈，还是稳定后收敛到 Azure Pipelines。

## 7. 仍然开放的技术决策

1. 契约是否长期留在 `Application`，还是等第二个消费方出现再抽 `SmeDemo.Contracts`
   （审计建议后者）。
2. 安全基线取舍：PCI 范围（确认不引入 PAN/CVV/PIN）、MFA/step-up、WAF/SIEM、
   HSM/KMS、数据保留、欺诈监控。
3. gitleaks 目前只扫工作树（`--no-git`，CI 是浅 checkout）；建议定期对完整 git 历史
   单独扫一次，或在上线前做一次历史审计。
4. `openapi/v1.json` 没有 `servers` 字段（运行时由请求推导，契约测试里做了归一化）。
   如果客户端需要固定 base URL，可以在生成时注入 `PUBLIC_BASE_URL`。
5. `docs/requirements.md` §5.4 里“BOM 数量”一类易漂移的数字已改为模糊表述；后续
   文档避免写会随代码变化的计数。

## 7b. 收尾事项（全部工作完成后再做）

- 把 `progress/` 里**长期有效**的内容整理成英文后放入 `docs/`（面向领导和同事），
  候选：基线状态摘要、PR 2/PR 3 的范围与验收标准、Azure 交付线决策、以及仍然开放的
  技术决策。`progress/` 继续只留本机中文流水账和排障细节。
- 现在不做：`docs/` 是对外英文材料，不混入中文工作笔记；等 PR 2/PR 3 落定后一次性整理，
  避免写两遍。

## 8. 恢复步骤

```powershell
git status -sb
git log -3 --oneline
dotnet build SmeDemo.slnx --nologo
dotnet test SmeDemo.slnx --nologo -v q
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
```

预期：HEAD `a49cbfd`（或其后提交），工作区干净，143 测试全绿。

需要跑完整 smoke 时：

```powershell
Copy-Item .env.example .env -Force   # 仅当 .env 不存在
docker compose up -d --build mssql azurite db-migrator db-migrator-auth db-grants auth api
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

## 9. 本机 clone 的关系

- `D:\GitHub\sme-demo`：主仓库，`origin` = `https://github.com/jiaqing123/sme-demo.git`。
- `D:\GitHub\sme-demo-ai`：实验 clone，分支 `experiment/ai-search`，`upstream` 指回
  主仓库（push 已禁用）。已把 PR 1 合并进去；后续两边保持 `fetch upstream` + merge
  的方式同步，不要互相 push。

## 10. 工作方式约定（沿用）

- 主代理负责规划、拆分任务、复核关键 diff、最终提交。
- 大规模代码检查/实现交给子代理，避免主对话上下文被细节灌满。
- 每个 PR 至少：实现子代理自测 → 独立验证子代理审查 + 复跑 → 主代理复跑并提交。
- 提交前检查 secrets、依赖、`bin/obj`、`git diff --check`。
- 不追“已知差异”，不引入 MediatR/AutoMapper/MVC/K8s；保持演示项目可在普通笔记本运行。
