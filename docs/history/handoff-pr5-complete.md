# 交接：PR 5 已完成（交付节奏与每周扫描）

> 生成于 2026-11-14 傍晚。仓库 `D:\GitHub\sme-demo`，分支 `main`，
> HEAD `bdb11be`，已 push。PR 5 把“每次改动 / 每周 / 每次发布”三层节奏写进
> 仓库，并加了每周容器扫描和 PR 模板。

## 1. 当前状态

- 分支 `main`，HEAD `bdb11be`，与 `origin/main` 同步；工作区干净（`progress/` 忽略）。
- GitHub Actions run `43567702655`（`bdb11be`）**5 个 job 全部 success**。
- 本周无需再复跑构建/测试：PR 5 只改文档、workflow、PR 模板，没有应用代码。
- 新增 scheduled workflow 还**没有实际运行过**：
  - 首次计划运行：**2026-11-17 03:00 UTC（周二）**；
  - 也可以在合并后用 `gh workflow run security-scan.yml` 手动触发；
  - 本机没有安装 `gh`，所以本轮只做了 actionlint 校验。
- Azure Pipelines 仍未排队；部署阶段仍是占位。

## 2. 提交

| 提交 | 内容 |
|---|---|
| `bdb11be` | `docs/delivery.md`、`.github/workflows/security-scan.yml`、`.github/pull_request_template.md`；更新 README / development / azure-pipelines / reference README / DoD / ADR-018；修正 `--ignore-unfixed` 的错误描述和过期 CI 状态 |

## 3. 三层节奏（现在以 `docs/delivery.md` 为准）

| 周期 | 做什么 |
|---|---|
| 每次改动 / PR | CI 五个 job 自动跑；本地 pre-flight 见 delivery §2；绿了再 merge |
| 每周 | 周一 Dependabot 分组 PR；周二 03:00 UTC 定时重建 6 镜像并跑 Trivy；triage 可修复的 HIGH/CRITICAL |
| 每次发布 | 只晋升已通过 CI 的同一 digest；Test/UAT/Prod 冒烟；记录 digest/SBOM/scan/审批；按 digest 回滚 |

PR1–PR4 是一次性能力建设，不需要每次发布重做；以后由 CI 和 `docs/delivery.md`
固化。

## 4. 本轮新增/修改的关键文件

- `docs/delivery.md`：三层节奏、release checklist、build-once/promote-digest、
  branch protection 设置清单、脱离 DSH 的操作模型、当前缺口。
- `.github/workflows/security-scan.yml`：
  `schedule: 0 3 * * 2` + `workflow_dispatch`；重建 6 镜像，Trivy 版本/校验和/
  扫描参数与 `ci.yml` 完全一致；不重复生成 SBOM（CI 的 `images` job 负责）。
- `.github/pull_request_template.md`：Summary / Scope / Verification / Checklist，
  指向 Definition of Done。
- `docs/reference/adr/ADR-018...`：修正“unfixed findings are reported”的说法；
  `--ignore-unfixed` 实际会把 unfixed 从输出里过滤掉，不是“记录但不失败”。
  同样的错误描述也已在 `ci.yml`、`security-scan.yml`、azure-pipelines.md、
  development.md 修正。
- `docs/development.md` / `docs/reference/definition-of-done.md`：CI 状态更新为
  PR 4 的 run `43566055703`；不再保留“updated workflow has not yet run”的过期说法。

## 5. 验证记录

- `actionlint`（`rhysd/actionlint:latest`，v1.7.12）对全部 workflow：**0 diagnostics**。
- `security-scan.yml` 与 `ci.yml` 的六条 docker build、Trivy 版本/校验和、
  扫描 flag/顺序逐项比对一致。
- 所有改动 Markdown 的相对链接（含锚点）：0 broken。
- `git diff --check`：通过；无 secrets、无 bin/obj、无 BOM/CRLF/tab。
- GitHub Actions run `43567702655`：五个 job 全绿。

## 6. 仍未做 / 需要人工动作

1. **Branch protection 必须手工设置**（GitHub 仓库设置或 ruleset）：
   - require PR + 1 approval + dismiss stale；
   - require 五个 check：`.NET build and tests`、`Web build and tests`、
     `Compose validation`、`Build and scan container images`、`Compose smoke test`；
   - require up-to-date、conversation resolution、block force push/delete；
   - 保护 `v*` tag。
   设置清单在 `docs/delivery.md` §5。
2. **Azure 部署线**：需要 `handoff-pr3-complete.md` §6 的 5 个决策；部署阶段
   仍是占位。发布 checklist 已写，但 Test/UAT/Prod 尚未实现。
3. **ACR 发布路径**：`publishImages=true` 会 push 到 ACR，但该路径不生成 SBOM、
   不跑本地 Trivy；发布时必须对 promoted digest 做 registry-side scan。
4. **Scheduled workflow 的首次运行**：等 2026-11-17 或手动 dispatch；如果 60 天
   无仓库活动，GitHub 会自动停用 schedule，需要重新启用。
5. **web 的 shell/wget、DHI 评估、分布式限流、/readyz 加固、Deprecation/Sunset、
   OpenAPI servers、gitleaks 全历史、契约层归属、HttpClient 捕获**：见
   `handoff-pr4-complete.md` §6，仍未做。

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

完整栈与 smoke 见 `RESUME.md`。

## 8. 工作方式（更新）

- 日常按 `docs/delivery.md`：PR 门禁、每周依赖/漏洞巡检、发布晋升。
- 主代理负责规划、拆分、复核、提交；实现/验证交给子代理。
- 不依赖 DSH：仓库文档 + CI + workflow 是唯一事实来源；`progress/` 只是本地记录。
