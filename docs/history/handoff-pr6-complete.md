# 交接：PR 6 已完成（服务端收尾小项）

> 生成于 2026-11-14 傍晚。仓库 `D:\GitHub\sme-demo`，分支 `main`，
> HEAD `10f1c50`，已 push。PR 6 修掉了 captive transient `HttpClient`，
> 补了真实 readiness probe 覆盖、OpenAPI `servers`、gitleaks 全历史扫描。

## 1. 当前状态

- 分支 `main`，HEAD `10f1c50`，与 `origin/main` 同步；工作区干净（`progress/` 忽略）。
- 测试 **241 passed / 0 failed**（Api 89、Auth 74、Application 25、Infrastructure 16、
  SmsMock 15、IssuerMock 8、Architecture 8、Migrator 6）。
- `dotnet build` 0 warning / 0 error；`dotnet format`、`docker compose config -q`、
  17 项目漏洞扫描、actionlint 全过。
- 完整 Compose 栈冷启动：7 服务 healthy、3 个 one-shot exit 0、smoke **7/7 PASS**
  （新增 `/readyz` 真实三探针检查）；issuer sync 3 张卡全部成功，`app.issuer_sync`
  3 行 `ok`。
- `openapi/v1.json` 现在带 `servers[0].url = http://localhost:8082`，运行时文档与
  提交文件完全一致。
- gitleaks 全历史扫描：52 commits、无泄露；GitHub Actions / Azure Pipelines 的
  checkout 都改成 full history。
- GitHub Actions：run `43572185168`（`659df81`）成功；run `43572436833`
  （`10f1c50`，修正 `.gitleaks.toml` 注释）成功。
- Azure Pipelines 仍未排队；部署阶段仍是占位。

## 2. 提交

| 提交 | 内容 |
|---|---|
| `659df81` | 修 `IssuerSyncService` 捕获 transient typed `HttpClient`；新增 6 个真实 metadata probe 测试；Compose smoke 增加真实 `/readyz` 三探针断言；新增 `ServerDocumentTransformer` 并重新生成 `openapi/v1.json`；gitleaks 改全历史；修正 `--ignore-unfixed` 与文档 |
| `10f1c50` | `.gitleaks.toml` 头部注释从 working-tree 改为 full-history（独立验证发现的唯一小问题） |

## 3. 关键实现

- `IssuerSyncService.SyncCardAsync` 现在每个 card 建一个 async scope，从同一 scope
  解析 `IIssuerClient`、`AppDbContext`、`ITransactionStore`；`IIssuerClient` 不再
  由 singleton 字段捕获。失败路径仍用独立 scope 写 `app.issuer_sync`。
- `tests/SmeDemo.Api.Tests/ReadinessProbeTests.cs`：6 个用例直接测
  `AuthMetadataReadinessProbe`（有效/错误 issuer/相对 jwks_uri/HTML content type/
  HTTPS 要求/`+json`）。
- `tools/smoke/compose-smoke.sh`：在原有 `wait_for_probe` 之后拉取一次 `/readyz`，
  断言 `status=ready` 且 checks 正好是 `database`、`blob-storage`、`auth-metadata`，
  全部 `ready`。
- `ServerDocumentTransformer` 从 `context.ApplicationServices` 取 `ApiOptions`，
  把 `document.Servers` 设成 `PUBLIC_BASE_URL`；构建时默认 `http://localhost:8082`。
  `OpenApiContractTests` 现在直接比较运行时和提交文档（不再移除 `servers`）。
- gitleaks：`.github/workflows/ci.yml` 的 dotnet job `fetch-depth: 0`，
  `azure-pipelines.yml` 的 DotNet job `fetchDepth: 0`，命令去掉 `--no-git`。
- 顺带修正 `--ignore-unfixed` 的长期错误描述：它会把 unfixed 从输出中过滤掉，
  不是“记录但不失败”。

## 4. 验证记录

- `dotnet test` 241/241；新增 7 个测试（6 probe + 1 OpenAPI server）。
- `bash tools/smoke/compose-smoke.sh`：7 PASS / 0 FAIL。
- `GET /readyz` 实际 body：
  `{"status":"ready","checks":[{"name":"database","status":"ready"},{"name":"blob-storage","status":"ready"},{"name":"auth-metadata","status":"ready"}]}`。
- issuer sync 日志 3 张卡分别 14/13/13 条交易；DB 3 行 `LastStatus=ok`。
- `GET /openapi/v1.json` 的 `servers` 与提交文件一致。
- gitleaks `zricethezav/gitleaks:v8.30.1` 全历史 52 commits，无泄露。
- actionlint 0 diagnostics；Azure YAML 解析通过。
- CI run `43572185168`、`43572436833` 均 success。

## 5. 仍未做

1. **web 的 shell/`wget`**：nginx 仍有 shell；DHI 评估仍是后续项。
2. **分布式限流**（Redis）与 metadata 缓存失效（ADR-017 生产化前置）。
3. **`/readyz` 加固**：仍匿名、无限流，仅 2s 缓存；真实 probe 已有 smoke 覆盖，
   但没有独立的限流/暴露策略。
4. **版本/弃用**：`Deprecation`/`Sunset` header 机制仍无。
5. **契约层归属**：继续留在 `Application` 还是抽 `SmeDemo.Contracts`，未决定。
6. **Azure 交付线**：部署阶段仍占位；需要 `handoff-pr3-complete.md` §6 的 5 个
   决策（其中 1–3 是 Test/UAT/Prod 的前置）。
7. **Branch protection 仍要手工配置**：清单在 `docs/delivery.md` §5。
8. **定时扫描首次运行**：2026-11-17 03:00 UTC；尚未实际跑过。
9. **VS Fast mode 实际 F5**：仍未跑过，只验证了 `--target base` 的内容。
10. **`IssuerSyncService` 的 per-card client 创建没有专门单测**：真实栈已验证，
    但单测只通过 fake `IIssuerClient` 间接覆盖；后续可加一个 scope-lifetime 测试。

## 6. 恢复步骤

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
bash tools/smoke/compose-smoke.sh
docker compose down -v --remove-orphans
```

gitleaks 全历史：

```powershell
docker run --rm -v "D:\GitHub\sme-demo:/repo" -w /repo zricethezav/gitleaks:v8.30.1 `
  detect --source . --config .gitleaks.toml --redact --exit-code 1
```

## 7. 工作方式（沿用）

- 日常按 `docs/delivery.md`：PR 门禁、每周依赖/漏洞巡检、发布晋升。
- 主代理规划/复核/提交；实现和独立验证交给子代理。
- 不依赖 DSH：仓库文档 + CI + workflow 是事实来源；`progress/` 只是本地记录。
