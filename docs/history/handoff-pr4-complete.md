# 交接：PR 4 已完成（容器硬化收尾）

> 生成于 2026-11-14 傍晚。仓库 `D:\GitHub\sme-demo`，分支 `main`，
> HEAD `d020a1e`，已 push。上一份交接 `handoff-pr3-complete.md` 的 §5 第 1 项
> （DHI/chiseled、去 shell/curl、外部探针）已完成 .NET 侧；`web`、DHI 评估、
> 分布式限流等仍未做。

## 1. 当前状态

- 分支 `main`，HEAD `d020a1e`，与 `origin/main` 同步；工作区干净（`progress/` 忽略）。
- 构建：`dotnet build SmeDemo.slnx` **0 warning / 0 error**。
- 测试：**234 passed / 0 failed**（Debug；Release 未在本轮复跑）。
- `dotnet format --verify-no-changes`、`docker compose config -q` 通过。
- `python tools/smoke/check-vulnerabilities.py`：**17 个项目 PASS**（新增 HealthProbe）。
- 完整 Compose 栈（含 web）已从 `down -v` 冷启动验证：
  - 7 个服务 healthy，migrator ×2 + db-grants 均 exit 0；
  - `compose-smoke.sh` 6/6 PASS；nginx :8080 登录/OTP/me/cards/art 全通；
  - `X-Request-Id` 回显；api/auth 单行 JSON 日志含 `request_id`/`TraceId`/`SpanId`；
  - 2 MB 登录 body 直连 :8081 返回 413 `application/problem+json`；
    :8080 是 nginx 自己的 HTML 413（1 MB `client_max_body_size`，既有行为）。
- 5 个 .NET 镜像确认无 `/bin/sh`、无 `curl`、无包管理器；探针可用；
  auth-keys 卷属主 `1654:1654`；6 个镜像 Trivy（HIGH/CRITICAL、ignore-unfixed）**0 发现**。
- GitHub Actions run `43566055703`（`d020a1e`）**5 个 job 全部 success**：
  .NET build/tests、Web、Compose validation、Build and scan container images
  （6 份 SBOM + Trivy）、Compose smoke。Azure Pipelines 仍未排队。
- 视觉验证缺口：VS Fast mode 的 `base` 镜像已确认包含探针和 `/keys`，但没有实际
  跑过 F5 附加调试。

## 2. 提交

| 提交 | 内容 |
|---|---|
| `d020a1e` | 五个 .NET 镜像换 chiseled-extra；新增 `tools/HealthProbe` 并替换 Compose 的 `CMD-SHELL curl`；auth `/keys` 改由 SDK 阶段创建并 `COPY --chown`；ADR-019 + 全部相关文档 |

## 3. 关键实现

- `tools/HealthProbe/`：无依赖控制台程序。参数必须是一个绝对 http(s) URL；
  2xx → 0，非 2xx/传输失败 → 1，用法错误 → 2；5 秒超时；
  用 `HttpCompletionOption.ResponseHeadersRead`，不下载响应体。
- 五个 Dockerfile 都在 SDK 阶段 `dotnet publish` 探针到 `/healthprobe`，再 COPY
  进运行时；api/auth 的 COPY 在 `base` 阶段（VS Fast mode 只构建 `base`）。
- auth 的 `/keys` 在 SDK 阶段 `mkdir`，在 `base` 里
  `COPY --from=healthprobe --chown=$APP_UID:$APP_UID /keys /keys`。
- Compose 的 healthcheck 改成
  `["CMD", "dotnet", "/healthprobe/SmeDemo.HealthProbe.dll", "<url>"]`；
  web 仍用 nginx+`wget`，mssql/azurite/db-grants 未动。
- 基础镜像：`aspnet:10.0-noble-chiseled-extra`（4 个服务）和
  `runtime:10.0-noble-chiseled-extra`（migrator），都按 manifest-list digest 固定。
- 文档：新增 `docs/reference/adr/ADR-019-chiseled-runtime-images-and-health-probe.md`
  并更新 azure-pipelines/requirements/development/runbook/threat-model/DoD/README。

## 4. 关键结论：为什么必须 `-extra`

- 标准 `-chiseled` 设置 `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` 且不含 ICU。
- `Microsoft.Data.SqlClient` 6.1.7 在 invariant 模式下直接抛
  `Globalization Invariant Mode is not supported`；强制 `false` 则报
  `Couldn't find a valid ICU package installed`。
- migrator 在标准 chiseled 上连不上 SQL；`-extra` 正常。因此 api/auth/migrator
  必须 `-extra`；sms/issuer 不需要，但为了统一基线和行为也一起用了 `-extra`。
- 代价：镜像比标准 chiseled 大约 +50–70 MB（api 358 MB、auth 352 MB、
  sms/issuer 239 MB、migrator 304 MB）。

## 5. 验证时纠正过的文档坑

独立验证发现并已修正：

- `docker compose cp ... api:/tmp/probe` **不能**证明 `/tmp` 可写：只要 rootfs 是
  read-only，Docker 拒绝往容器内任何路径 copy（包括 `/tmp`）。文档已改成用
  `docker inspect` 看 tmpfs，并明确 copy 只适合从容器往外拿。
- 探针的 stderr **不在** `docker compose logs`，而在
  `docker inspect --format '{{json .State.Health}}'` 的 Health.Log 里。
- 旧的 `touch /nope` 验证已删除（镜像里没有 `touch`）。
- `curl -f` 的退出码是 22，探针是 1；对 Compose 只关心是否非 0，文档不再说
  "same exit code"。

## 6. 仍然未做

1. **web 的 shell/`wget`**：nginx 镜像仍有 shell；要彻底去掉需要 shell-free web
   镜像或外部探针，单独立项。
2. **DHI 评估**：DHI Community 的 registry/条款/镜像布局未评估；ADR-019 记录为
   后续生产化候选。SLSA provenance 仍未做。
3. **平台安全上下文**：AKS/App Service/Container Apps 的 pod securityContext、
   编排器探针（直接 HTTP，不 exec 探针）仍需在 Azure 交付线落地。
4. **分布式限流**（Redis 等）与 metadata 缓存失效（ADR-017 已列为生产化前置）。
5. **`/readyz` 加固**：仍匿名、无限流，仅 2s 缓存；真实 probe 的集成覆盖仍缺
   （测试用 fake probe）。
6. **版本/弃用策略**：`Deprecation`/`Sunset` header 机制或示例未做。
7. **OpenAPI `servers`**：仍由运行时推导。
8. **gitleaks 完整历史**：CI 只扫工作树（浅 checkout）。
9. **契约层归属**：继续留在 `Application` 还是抽 `SmeDemo.Contracts`，未决定。
10. **Azure 交付线**：`azure-pipelines.yml` 部署阶段仍是故意失败的占位；开工前
    需要 `handoff-pr3-complete.md` §6 的 5 个决策。
11. **Hosted singleton 捕获 transient `HttpClient`**：仍在。
12. **VS Fast mode 实际 F5 调试**：本轮只验证了 `--target base` 的镜像内容。

## 7. 恢复步骤

```powershell
git status -sb
git log -3 --oneline
dotnet build SmeDemo.slnx --nologo
dotnet test SmeDemo.slnx --nologo -v q
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
python tools/smoke/check-vulnerabilities.py
```

完整栈：

```powershell
docker compose up -d --build
docker compose ps
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

镜像扫描命令与缓存卷见 `RESUME.md`。

## 8. 工作方式（沿用）

- 主代理规划、拆任务、复核 diff、提交；实现/验证交给子代理。
- 每个 PR：实现子代理自测 → 独立验证子代理复跑 + 审查 → 主代理复跑、提交、push。
- 不追“已知差异”，不引入 MediatR/AutoMapper/MVC/K8s；演示项目保持笔记本可跑。
