# Golden 夹具来源与用途（WS-B: FSRS-6 纯 C# 核心验证）

## 文件

| 文件 | 字节 | SHA-256 |
|---|---|---|
| `scenarios.json` | 49,329 | `c252e2e273ccc2c47b1edc25d52e3119cb351a0e1d544f148dc4c047882f8b99` |
| `expected.csv` | 43,639 | `41afede262973f86895efd0bb92cecea760d82b3e3aa68ef675b74515877f282` |

两个文件的 SHA-256 已与上游 pin 住的 commit **逐字节比对一致**（下载后 `shasum -a 256` 复核）。

## 来源

- 仓库：`https://github.com/Overmiind/FSRS-Sharp`（NuGet 包 `Fsrs.Sharp` 2.0.0 的源码仓库）
- commit SHA：**`871dfa092e7dfde95af25c3e31114bb8311ced23`**（`main`，2026-09-05；
  与 NuGet 2.0.0 nupkg 的 SourceLink commit 一致，见 `agents/audit-05-fsrs-upstream.md` §2 路线 B）
- 路径：`Tests/Golden/scenarios.json`、`Tests/Golden/expected.csv`
- 取法（可复现）：
  ```bash
  curl -sS -o scenarios.json https://raw.githubusercontent.com/Overmiind/FSRS-Sharp/871dfa092e7dfde95af25c3e31114bb8311ced23/Tests/Golden/scenarios.json
  curl -sS -o expected.csv   https://raw.githubusercontent.com/Overmiind/FSRS-Sharp/871dfa092e7dfde95af25c3e31114bb8311ced23/Tests/Golden/expected.csv
  ```
- 上游 license：**MIT**（`gh api repos/Overmiind/FSRS-Sharp --jq .license.spdx_id` → `MIT`）

## 生成工具

上游 README 声明：golden 输出由**参考实现本身**产生，即
**py-fsrs 6.3.2**（`open-spaced-repetition/py-fsrs` tag `v6.3.2`，commit
`9446cb06605c597a063aeee49f7d188d42e34dc2`），配置为官方默认参数、
`enable_fuzzing = False`、`learning_steps = [1min, 10min]`、`relearning_steps = [10min]`。

夹具内容：64 个场景 / 692 次复习，每行给出
`scenario, index, state(1=Learning/2=Review/3=Relearning), card_step, stability, difficulty, interval_seconds`。
场景刻意混合三种时间间隔：同日子日间隔（走 short-term 稳定性路径）、整日间隔、
以及小数日间隔（3.875 / 10.58 天，专门用来暴露「浮点天累加被截断错一位」的坑）。

## 我们用它验证什么

1. **`Fsrs6Model` 的全部公式与常量**（`lexi_avalonia/Application/Memory/Fsrs6Model.cs`）：
   692/692 行的 `stability` / `difficulty` 命中（实现在 Review 态行上另比 `interval_seconds`，
   399/399 命中；另 293 行排的是 learning/relearning step，属生产路径不接管的轮内强化，
   已在测试输出里显式计数说明，不静默跳过）。
2. **时间轴口径**：所有场景的时间戳按 `now += AddHours(offset_hours)` 累积，用来确认
   实现「整天截断 + 整数 ticks 相减」的口径与参考实现一致（audit §10 硬性要求 2）。
3. **`Fsrs6Reference`（py-fsrs 参考状态机）**：把同一份夹具喂给含 learning steps 的状态机，
   692/692 行 `state` + `card_step` + `stability` + `difficulty` + `interval_seconds` 五列全中——
   这同时反证了「D/S 轨迹与 state/learning steps 无关」，因此生产路径（steps 为空）可以
   用同一份夹具锁死。

## 不用于什么

- 夹具**只是数据**，不含可执行代码；不引入任何 NuGet / 原生依赖（`Fsrs.Sharp` 2.0.0 本身
  **不进入**生产依赖，见 `dependency_decision.md`）。
- 夹具不覆盖 optimizer / 参数训练；本升级的 optimizer 相关能力另行处理。
