# 交接：从演示 API 到参考基线

> **已被 `progress/handoff-pr1-complete.md` 取代（2026-11-11 晚，PR 1 提交后）。**
> 本文件保留为 PR 1 开工前的状态快照；继续工作请看新交接文件。

> 生成于 2026-11-11（会话结束时）。仓库 `D:\GitHub\sme-demo`。
> 下次新对话先读本文件，再决定是否按 PR 1 开工。

## 0. 新增硬性要求（2026-11-11，休息前）

公司是金融类机构，系统按“很可能被攻击”设计。**安全是发布门禁**，不是收尾加固。

- 规范文档：`docs/threat-model.md`（资产、信任边界、STRIDE、滥用用例、必备控制、生产差距清单）。
- 需求入口：`docs/requirements.md` §11（安全章节，v0.5）；README 顶部有 “not production” 警示。
- 新端点、新数据存储、新第三方、认证/授权变更、新 PII/金融数据，必须先做威胁模型评审 + 滥用用例测试。
- 所有 demo 捷径列在 `docs/requirements.md` §11.3 与 `docs/threat-model.md` §7，禁止静默进入生产。
- 已提交：`949f853 Add financial-sector threat model and security release gate`。

## 1. 当前状态

- HEAD：`949f853 Add financial-sector threat model and security release gate`
  （其下是 `d182cdd` 端点重构提交）
- 分支：`main`，比 `origin/main` 领先 2 个提交（**未 push**）。
- 工作区：干净。端点重构 + BOM/using 清洁已提交（48 个文件，507+/222-）。
- 构建：`dotnet build SmeDemo.slnx` **0 warning / 0 error**；Release 也验证过。
- 测试：**126 passed / 0 failed**
  - SmeDemo.Api.Tests 20（原 17 + 契约测试 3）
  - SmeDemo.Auth.Tests 36
  - SmeDemo.Application.Tests 25
  - SmeDemo.Infrastructure.Tests 16
  - SmeDemo.SmsMock.Tests 15
  - IssuerMock.Tests 8
  - Migrator.Tests 6
- `dotnet format --verify-no-changes` 本机通过，但仓库还没有 `.editorconfig`，CI 也没有守住格式。
- `docker compose config -q` 通过；`docker compose build api` 本机成功；**完整 `docker compose up` 未验证**。
- GitHub Actions 远端**未实际运行验证**（只读了 workflow 文件）。

## 2. 本次完成的内容

### 2.1 端点重构（`d182cdd` 的主体）

原先 `ResourceEndpoints.cs` 一个文件承载全部端点、claims 解析、缓存头；重构后：

| 文件 | 职责 |
|---|---|
| `src/SmeDemo.Api/Endpoints/ApiRoutes.cs` | 路由常量（`Prefix = "/api/v1"`） |
| `src/SmeDemo.Api/Endpoints/ResourceEndpoints.cs` | 只做端点组合 |
| `src/SmeDemo.Api/Endpoints/MeEndpoints.cs` | `/me`、`/me/cards`、`/me/cards/{id}/transactions` |
| `src/SmeDemo.Api/Endpoints/CardEndpoints.cs` | `/cards/{id}/art` |
| `src/SmeDemo.Api/Endpoints/HealthEndpoints.cs` | `/healthz` |
| `src/SmeDemo.Api/Endpoints/TransactionQuery.cs` | `[AsParameters]` 查询对象 |
| `src/SmeDemo.Api/Auth/CurrentUser.cs` | `BindAsync` 强类型身份 |
| `src/SmeDemo.Api/Auth/AuthPolicies.cs` | `ApiUser` 策略名 |
| `src/SmeDemo.Api/Auth/OrgRequirement.cs` / `OrgAuthorizationHandler.cs` | org 授权要求 |
| `src/SmeDemo.Api/Auth/ProblemAuthorizationResultHandler.cs` | 403 统一 ProblemDetails 信封 |
| `src/SmeDemo.Api/Http/CacheControlMetadata.cs` | 端点声明 Cache-Control |

关键点：

- `Program.cs` 用 `FallbackPolicy` 默认拒绝；`/healthz` 与 Development 的 `/openapi/v1.json` 显式 `AllowAnonymous()`。
- 删除 `OrgClaimMiddleware.cs`、`UserClaims.cs`；授权改为命名策略 + `IAuthorizationRequirement`。
- 每个 API 端点都有 `.WithName/.WithSummary/.Produces/.ProducesProblem`，返回 `Task<Ok<T>>` + `TypedResults`。
- 缓存策略通过 endpoint metadata + 一个中间件统一写头；`/me*` 成功与 401/403 都是 `no-store`；art 只有成功响应带 `private, max-age=3600`。
- 新增 `tests/SmeDemo.Api.Tests/EndpointContractTests.cs`：精确断言 4 条 API 路由、`ApiUser` 策略、端点名、缓存 metadata、OpenAPI 路径、`/me` 401 的 `no-store`、art 错误不继承 private 策略。

### 2.2 代码清洁

用户随后做了一轮 UTF-8 BOM、using 排序/删除冗余 using 的清洁，已一并提交。提交前由子代理确认：**未发现语义变化**，删除的 using 均冗余，126 测试仍全绿。

### 2.3 验证方式

- 实现子代理：重构 + 自测。
- 验证子代理：独立审查 diff + 复跑 build/test。
- 提交前审查子代理：审查 48 个 staged 文件，确认无 secrets/依赖/bin/obj 混入。
- 主代理：复核关键文件、亲自复跑 build/test、提交。

## 3. 关键决策（不要回退）

1. **Minimal API + 显式约定**，不换 MVC/FastEndpoints。端点保持静态方法 + `TypedResult`。
2. **原生 DI**：直接注入 `IQueryHandler<,>`，不引入 MediatR/AutoMapper/通用仓储/UnitOfWork。
3. **默认拒绝**：`FallbackPolicy` 要求认证；公开端点必须显式 `AllowAnonymous()`。
4. **org 校验是授权策略**，不是路径前缀中间件。
5. **缓存策略是端点元数据**，不在端点里直接写 `HttpResponse`。
6. **契约测试可执行**：路由、授权、响应、缓存、OpenAPI 都有断言；后续新增端点必须过这些测试。
7. **不引入 K8s/微服务/消息队列**；Compose 就是部署单元，requirements 明确超范围。
8. **安全是发布门禁**：威胁模型评审 + 滥用用例测试 + 无新增 PII/秘密，才允许合并涉及端点/数据/第三方的改动（`docs/requirements.md` §11）。

## 4. 已知且有意的差异（不要修）

| 差异 | 现状 | 处置 |
|---|---|---|
| 未知路由 + wrong-org token | 旧 403 → 新 404 | 保留 404；符合 `NOT_FOUND` 语义，不泄露路由 |
| 非 GUID `sub` 调 `/me` | 旧 200 → 新 401 | 保留 401；畸形 token 按未认证处理 |
| `MapGroup("")` 的 `RoutePattern.RawText` | 生成 `/api/v1/me/`，匹配无差异 | 契约测试已归一化，不改路由 |
| art 404 的框架 `no-cache` | ASP.NET Core 异常处理本来就会写 | 保留；测试只断言不继承 art 的 private 策略 |

如果团队需要书面记录，写进 ADR 即可，不要改代码。

## 5. 参考基线审计结论（2026-11-11）

### 5.1 已满足

- 分层与引用方向符合 `docs/requirements.md` §5.5；端点只注入 `IQueryHandler` + `CurrentUser`。
- 授权、错误信封基础、缓存元数据、DB 最小权限、密钥隔离、优雅停机、EF 重试、issuer 启动重试都高于普通 demo。
- CI 已有 Release build/test、EF pending model、SQL 脚本 diff、MediatR/AutoMapper 守卫、web build。
- Dockerfile 多阶段、非 root；`.dockerignore` 排除 bin/obj/.env/keys。

### 5.2 六个 P0 差距

1. **CI 交付链未验证**：`ci.yml` 只 build/test + `docker compose config -q`，不构建镜像、不 `compose up`；Dockerfile、healthcheck、migrator/grants 顺序在 CI 全未验证。
2. **OpenAPI 只是运行时端点**：无构建期产物、无 CI diff、无 Bearer `securitySchemes`；客户端无法生成带 token 的调用。
3. **错误信封不完整**：`ProblemResults` 无 `traceId`/`instance`；405 被 fallback policy 变成 401；缺 405/429 矩阵测试。
4. **`/healthz` 只是 liveness，却被 Compose 当 readiness**：DB/Blob/auth 挂了仍 healthy；没有 `/readyz`。
5. **auth 安全基线缺失**：无 per-IP/per-username 限流；auth 端点默认匿名；没有 auth 的 HTTP 集成测试（只有 service 级测试）。
6. **供应链/运行环境未加固**：基础镜像浮动 tag、应用镜像 `:local`、无漏洞/镜像扫描、无 Dependabot/Trivy、Compose 无 `read_only`/`cap_drop`/资源限制。

补充 P0：`ValidateOnBuild/ValidateScopes` 未全环境开启；`AuthOptions` 不走 `IConfiguration`；`ApiTestFactory` 用进程级环境变量。

### 5.3 P1/P2 摘要

**P1**

- `art_url` 在 `GetCardsQuery.cs` 硬编码 `/api/v1`；应移到 Api 并用路由常量/`LinkGenerator`。
- JWT metadata/网络故障被映射成 401 `INVALID_TOKEN`，应为 503 或启动期 fail-fast。
- API 直连端口没有安全响应头；art 是 SVG，直连时缺 `nosniff`/CSP。
- 无架构测试（端点纯净度、引用方向、授权/契约元数据）；无覆盖率门槛；无 OpenAPI 快照。
- Compose 未加固；CI 无 format/覆盖率/漏洞扫描；无 `global.json`。
- 文档声称的 health checks / JSON console 实际未实现；`requirements.md` 把 Swashbuckle 说成 built-in；`development.md` 的 CI 段过期。
- 缺 ADR 索引、Definition of Done、“新增一个资源”cookbook、运维 runbook。

**P2**

- `AuthOptions`/`ApiOptions` 配置读取重复；考虑 `appsettings.json` 非敏感默认。
- `AddJsonConsole`（或删掉“JSON console”声明）。
- `Messaging.cs` 的 command 侧无实现、无测试；要么补示例，要么标注“预留”。
- `LICENSE` / `THIRD-PARTY-NOTICES` 缺失。
- `docker-compose.dcproj` 干扰 solution 级 `dotnet list package`。
- `IssuerSyncService` 的 `DateTime.UtcNow` 可改 `TimeProvider`；web 缺 lint。

### 5.4 未验证项

- 完整 `docker compose up`（mssql/azurite/migrators/auth/web/端到端登录）。
- GitHub Actions 远端执行。
- 前端 `npm ci` / `npm test` / `npm run build`。
- 镜像 CVE 扫描。
- `dotnet format` 输出的 workspace 警告（exit 0，未定位）。

## 6. 下一步：PR 1（尚未开工）

**目标：让约定变成 CI 会失败的东西，并证明镜像/Compose 真能跑；不改业务行为。**

### A. OpenAPI 产物化

- 构建期生成 `openapi/v1.json` 并提交；CI 重新生成后 `git diff --exit-code`。
- `AddOpenApi` 增加 document transformer，写入 Bearer `securitySchemes`；端点声明 `security`。
- 文档补生成/消费/校验方式。

### B. CI 交付门禁

- CI 增加镜像构建（api/auth/web/mocks/migrator）与应用镜像 `${GITHUB_SHA}` tag。
- 增加 `compose-smoke` job：起 mssql/azurite/migrators/auth/api → 等 healthy → 登录拿 token → `GET /api/v1/me`；失败时收集 `docker compose logs`。
- 增加 `dotnet format --verify-no-changes`，补 `.editorconfig`；保持现有 build/test 门禁。

### C. 可执行架构测试

- 新增 `tests/SmeDemo.Architecture.Tests`：
  - `Endpoints` 命名空间不得引用 EF/Azure/HttpClient。
  - `Application` 不得引用 `Api`/`Infrastructure`。
  - 每个 `/api/v1` 端点必须有 `IAuthorizeData`、`IEndpointNameMetadata`、至少一个 `IProducesResponseTypeMetadata`。
  - auth 端点必须显式 `AllowAnonymous` 或 `RequireAuthorization`（需要给现有 auth 端点补元数据，行为中性）。
- 测试必须能对注入的违规真正变红。

### D. 基线文档

- 新增 `docs/reference/`：基线说明、Definition of Done、ADR 索引、`cookbook-add-resource.md` 初稿。
- 修正 `requirements.md` 与实现不符的声明（health checks、JSON console、Swashbuckle）。
- 更新 `development.md` 的 CI 段，逐条对应 `ci.yml`。

### E. 安全门禁（与 A-D 同批）

- `docs/threat-model.md` 已先行提交；PR 1 把 §5 中“现状已可验证”的滥用用例落成测试：
  IDOR/BOLA（他人卡 403、未知卡 404）、错误 token（401 各 code）、日志不含
  password/OTP/token/phone、错误响应无堆栈。
- CI 增加按项目的依赖漏洞扫描与 secrets 扫描（绕开 `docker-compose.dcproj`）。
- Definition of Done 增加：威胁模型评审、滥用用例测试、无新增 PII/秘密、demo 捷径已登记。
- 限流 429、安全头、`/readyz` 的测试放到 PR 2；镜像扫描/容器加固放到 PR 3。

### PR 1 验收

- `openapi/v1.json` diff 干净，含 Bearer scheme。
- CI 的 compose smoke 绿；镜像按 SHA tag。
- 架构测试通过，且能对违规变红。
- `dotnet format --verify-no-changes` 门禁生效。
- 基线文档能让人不问你就能加一个只读资源。
- 安全 DoD 生效：威胁模型评审进入 PR 流程；至少 4 类滥用用例测试落地。

## 7. 之后的两刀

### PR 2：运行时契约与 auth 加固

- `traceId`/`instance` 进 ProblemDetails；405 信封；400/401/403/404/405/429/500/503 矩阵测试。
- 按 `docs/threat-model.md` §5 补齐限流 429、IDOR、token、安全头、日志脱敏的滥用用例测试。
- `/healthz`（liveness）+ `/readyz`（DB/Blob 依赖检查）；Compose healthcheck 改探 ready。
- 全环境 `ValidateOnBuild`/`ValidateScopes`。
- auth per-IP/per-username 限流 + `Retry-After`；auth 端点显式匿名/授权；auth HTTP 集成测试。
- API 安全头：`X-Content-Type-Options: nosniff`，art 加 CSP sandbox。
- JWT metadata 故障映射为 503。

### PR 3：交付与可观测性

- 基础镜像 digest 固定、应用镜像 SHA tag、Dependabot、Trivy、按项目漏洞扫描、`global.json`。
- Compose 加固：`read_only`、`cap_drop: [ALL]`、`no-new-privileges`、资源限制。
- OpenTelemetry（AspNetCore/Http/EF/Storage）+ JSON console + `X-Request-Id`。
- `AuthOptions` 走 `IConfiguration`；`art_url` 去硬编码；版本/弃用策略。
- runbook、LICENSE、THIRD-PARTY-NOTICES；修正 auth “demo-only” 的 README 警示。

## 8. 恢复步骤（新对话）

```powershell
git status -sb
git log -1 --oneline
dotnet build SmeDemo.slnx --nologo
dotnet test SmeDemo.slnx --nologo -v q
docker compose config -q
```

预期：HEAD `949f853`（或其后的 docs/后续提交），工作区干净，126 测试全绿。

恢复提示词可直接用：

> 读 `progress/archive/handoff-reference-baseline.md` 和 `docs/threat-model.md`。当前 HEAD 应为 `949f853` 或其后的提交，工作区干净。先核对 `git status/log` 和 `dotnet test`，然后按 PR 1 范围开工：OpenAPI 产物化、CI 镜像+compose smoke、架构测试、基线文档、安全 DoD。安全是发布门禁，新端点/新数据/新第三方必须先过威胁模型评审。实现和代码检查用子代理，主代理负责规划与复核。

## 9. 待用户确认

1. PR 1 范围是否照上面执行。
2. 版本策略：是否现在引入 `Asp.Versioning.Http`，还是保持 `/api/v1` 常量 + 文档规则。
3. `auth` 的 demo-only 警示已在 README 添加；是否需要在登录页/文档顶部再强化。
4. 是否补 `LICENSE` / `THIRD-PARTY-NOTICES`，或明确“内部仓库，不对外分发”。
5. 契约是否长期留在 `Application`，还是等第二个消费方出现再抽 `SmeDemo.Contracts`（审计建议后者）。
6. 安全基线取舍：`docs/threat-model.md` §10 的 PCI 范围、MFA/step-up、WAF/SIEM、密钥管理（HSM/KMS）、数据保留、欺诈监控。

## 10. 工作方式约定

- 主代理负责规划、拆分任务、复核关键 diff、最终提交。
- 大规模代码检查/实现交给子代理，避免主对话上下文被细节灌满。
- 每个 PR 至少：实现子代理自测 → 独立验证子代理审查 + 复跑 → 主代理复跑并提交。
- 提交前检查 secrets、依赖、`bin/obj`、`git diff --check`。
- 不追“已知差异”，不引入 MediatR/AutoMapper/MVC/K8s；保持演示项目可在普通笔记本运行。
