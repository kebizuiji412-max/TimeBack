# TimeBack Agent Protocol（协议 1.0 + 最小 CLI 实现）

> **状态：协议 1.0 不变，已有最小可用的 CLI 实现。**
>
> 当前 TimeBack 提供**一个** Agent 入口：独立的 `LastRegret.Agent.exe`（CLI + JSON，见第 13 节）。
> 除此之外**没有** MCP Server、没有 Plugin、没有 Skill、没有网络服务、没有 HTTP / IPC 服务端，
> 也没有常驻后台进程；GUI（`LastRegret.exe`）行为与用户视角完全不变。
> 本文件定义 **"外部 Agent 通过什么能力访问 TimeBack"**，是接口实现的协议依据。
>
> 机器可读的能力清单位于项目根目录：`agent-interface.json`。

---

## 1. 这是什么

TimeBack Agent Protocol 是 **TimeBack 自己定义**的一套平台无关的**能力协议**。

它描述的不是函数签名，也不是数据库表，而是**能力边界**：
"一个外部 Agent 可以要求 TimeBack 做什么，以及不可以要求它做什么"。

| 它是 | 它不是 |
|---|---|
| TimeBack 自有的、平台无关的能力声明 | 某个 AI 平台的 SDK 适配层 |
| 未来所有 Adapter（Native Plugin / CLI+JSON / Skill）的**共同下限** | MCP 协议本身，也不是 MCP 的方言 |
| 一份"Agent 能做什么"的白名单 | 一份"TimeBack 内部怎么实现"的说明书 |
| 与产品版本独立演进的协议版本 | 产品版本号，也不是发布版本号 |

**关键理解：Agent 面向的是「能力」，不是 TimeBack 的内部实现。**

| ✅ 正确 | ❌ 错误 |
|---|---|
| `timeline.read` | `MainViewModel.GetTimeline()` |
| `restore.preview` | 直接查 `snapshots` 表算差异 |
| `changes.read` | 直接打开 `events.db` 跑 SQL |
| `restore.execute` | 直接往被保护目录里写文件 |

禁止针对任何具体 Agent 平台做专门实现（OpenAI / Claude / Gemini / DeepSeek / 豆包 /
MCP / 任何第三方 Agent SDK）。协议必须由 TimeBack 自己定义。

---

## 2. 当前协议版本

| 项 | 值 |
|---|---|
| 协议名 | `timeback-agent` |
| 协议版本 | **1.0** |
| 协议状态 | `experimental`（协议 1.0 保持不变；已有最小 CLI 实现，见第 13 节） |
| 产品 | 回溯 / TimeBack |
| 产品版本 | **0.1.0**（见 `code/Directory.Build.props`） |
| 平台 | Windows |

协议版本与产品版本**各自独立演进**。产品发 1.0 不代表协议变成 1.0-x，反之亦然。

---

## 3. capability 的定义

一个 capability 是一个**稳定命名的能力标识**，满足三条：

1. **平台无关**：不出现任何具体 Agent 平台、库、协议的痕迹。
2. **有真实依据**：被声明的 capability 必须对应 TimeBack **当前已经真实存在**的能力。
   不允许为了"看起来完整"而提前声明尚未实现的能力。
3. **面向结果，不面向实现**：描述"能拿到什么 / 能改变什么"，
   不描述"调用哪个类、读哪张表"。

命名形式：`<域>.<动作>`，全小写，用 `.` 分隔。当前只有两个域：
`protected-folders` / `timeline` / `changes`（读取域）与 `restore`（恢复域）。

**没有被声明的能力就是没有被授权的**。Agent 不得因为"某能力显然迟早会有"就自行假设它存在。

---

## 4. 当前声明的 capabilities

见 `agent-interface.json`。共 6 项，全部对应 TimeBack 现有真实能力：

| capability | 读写 | 用途 | 对应的现有真实能力 |
|---|---|---|---|
| `protected-folders.read` | 只读 | 读取当前受保护的文件夹清单：路径、是否正在监听、是否已建立基线 | 设置页「保护文件夹」列表所展示的同一份事实 |
| `timeline.read` | 只读 | 读取某个受保护文件夹的恢复点（时间点）清单及其时间与规模 | 「历史」页时间线上的恢复点 |
| `changes.read` | 只读 | 读取变化记录：哪个路径在什么时候发生了什么变化 | 「历史」页的变化列表 |
| `restore.preview` | 只读 | 生成预览：恢复到某个时间点会创建 / 覆盖 / 删除哪些路径 | 恢复页第 2 步的预览计划 |
| `restore.execute` | **写入** | 执行一份已经预览并确认过的恢复计划（可含删除） | 恢复页第 3 步的「确认执行」 |
| `restore.undo` | **写入** | 撤销一次实际存在且可撤销的恢复操作 | 恢复页的「撤销」 |

关于 `readonly` 字段：这是清单里除 `id` / `description` 外唯一的字段，
它回答 Agent 最需要先知道的一个问题 —— **这个能力会不会改动磁盘**。
取值有真实依据，不是标注习惯：

- `restore.preview` **是只读的**：生成预览的代码路径只读取索引与快照并计算差异，
  不写磁盘、不创建快照、不写内容库。（`RestoreEngine` 的第一个写入点就是 `Execute`。）
- `restore.execute` / `restore.undo` **是写入的**：它们会改动磁盘上的用户文件。

---

## 5. Agent 与 TimeBack 的边界

```
AI Agent
   ↓
Agent Adapter            ← 由未来实现，本协议不规定它长什么样
   ├─ Native Plugin / API
   ├─ CLI / JSON
   └─ Skill fallback
   ↓
TimeBack Agent Interface ← 本协议定义的就是这一层的能力边界
   ↓
TimeBack Core
```

边界规则：

1. **Agent 只通过 capability 说话。** Adapter 负责把 capability 翻译成具体调用，
   Agent 自身不应知道 TimeBack 内部有哪些类、表、文件。
2. **TimeBack Core 不依赖任何 Agent 平台。** 方向永远是 Agent → TimeBack，
   TimeBack 不会反向依赖某个 Agent 框架、也不为某个平台做特化。
3. **Adapter 可以有很多个，能力只有一个。** 换 Adapter 不改变能力语义。
4. **TimeBack 不主动联系 Agent。** 本协议不引入网络服务、不引入回调、
   不引入常驻后台监听"Agent 指令"的通道。

---

## 6. Agent 不得直接访问的内部实现

以下是**实现细节**，不是接口。Agent 一旦依赖它们，TimeBack 就再也不能安全地修改自己的内部结构：

| 不得访问 | 原因 |
|---|---|
| SQLite 数据库与其表结构（`events.db` / `objects.db`，`snapshots` / `files` / `file_versions` 等） | 表结构是内部实现，会随版本变化；绕过引擎直接写会破坏时间线与恢复点的一致性 |
| 内容库的存储布局（`store/objects/<hash>` 之类的路径与文件格式） | 内容寻址存储的布局与编码方式是内部实现 |
| 快照清单（manifest）的内部文件格式 | 同上，且"内容不完整"这类判定必须由引擎给出 |
| WPF / 界面层（`MainViewModel`、窗口、绑定） | 界面是产品交互，不是接口；它会被重新设计 |
| 文件系统监听实现（`ReadDirectoryChangesW`、监听线程、通知缓冲区处理） | 属于引擎内部机制 |
| 直接读写受保护目录里的文件来"代替恢复" | 恢复必须经过恢复计划：差异计算、预览指纹、安全点、范围守卫 |
| 程序自身的数据目录 | 该目录被硬性禁止作为恢复目标 |

一句话：**Agent 不得绕过 TimeBack 现有的恢复安全机制。**

---

## 7. 接口形态：最小 CLI（已实现）

优先考虑**稳定的 CLI + JSON**：进程内一次性调用、stdin/stdout 传 JSON、
无网络端口、无常驻服务。它最容易审计、最容易限制权限，也最不打扰普通用户。

**最小可用实现已落地**（用法见第 13 节），形态与早期示例略有不同：

```
LastRegret.Agent.exe <command>        # 独立可执行文件，一次进程一次调用
```

早期文档里写过的 `TimeBack.exe agent capabilities` 只是"往哪个方向长"的示例；
真正落地时选了**独立可执行文件**，因为它让 GUI 与 CLI 的边界在进程层面就分开：
`LastRegret.exe` 至今**完全不解析命令行参数**，两条入口互不污染。

不论最终采用哪种形态，以下输出约定应当保持：

- 输出是可解析的 JSON，**字段只增不删**；
- 结果要能区分"成功 / 无变化 / 被拒绝 / 失败"，不允许把拒绝伪装成成功；
- 失败必须带一个**人能看懂的原因**，而不是内部异常文本；
- 不输出本机绝对路径之外的隐私内容（TimeBack 现有的崩溃日志已经会对本机路径打码）。

---

## 8. 危险操作必须经过明确的安全边界

**核心原则：Agent 接口不能因为是机器调用，就默认拥有"无确认的危险操作权限"。**

TimeBack 现有的安全机制是**现成的**，Agent 接口必须复用、不得绕过：

| 机制 | 内容 |
|---|---|
| **预览即契约** | 执行时传入的确认指纹必须与恢复计划的指纹一致；不一致会被拒绝，要求重新预览。也就是说：**确认的必须是同一份预览** |
| **执行前必有安全点** | 执行恢复前会先建立完整安全点，"这次操作整体可以撤销"不是承诺，是机制 |
| **删除需显式确认** | 计划里"删除目标时刻之后新增的路径"这类条目会被标记为需确认；未获确认时会被拒绝执行 |
| **空计划被拒** | 与当前状态一致的"恢复"会被拒绝，而不是假装成功 |
| **范围守卫** | 恢复动作受保护根范围约束；程序自身数据目录被硬性禁止；系统关键目录（`C:\Windows`、`C:\Program Files` 等）在预览中会被强提醒 |
| **可撤销性有据可查** | 只有确实存在安全点、且确实改动过文件的恢复操作才可撤销 |

由此推出的三条硬性要求：

1. **`restore.preview` 必须是只读的。** 预览不得产生任何副作用 ——
   不写磁盘、不建快照、不写内容库。预览可以被 Agent 任意调用而不产生后果。
2. **`restore.execute` 必须经过明确的恢复计划。** 不接受"恢复到某个时间点"这种笼统指令后
   由 Agent 直接动手；必须是"预览 → 拿到计划 → 确认 → 执行同一份计划"。
3. **`restore.undo` 只能撤销真实存在且可撤销的恢复操作。**
   不存在的、已被覆盖的、不可撤销的操作，必须明确拒绝，而不是"尽力而为"。

**本次不实现 Agent 权限系统，也不修改现有 UI。** 上面的要求是对未来实现的约束，
不是当前需要新增的功能。

---

## 9. 兼容性原则

未来接口实现应当遵守：

1. **capability 名一旦发布就不重命名、不复用。** 名字是协议的一部分；
   语义变了就换新名字，旧名字要么保留、要么按第 10 节废弃。
2. **只增不改语义。** 已有 capability 的含义不得在其生命周期内改变。
3. **未知 capability 必须被忽略，而不是失败。** 旧 Agent 遇到新 capability 应当跳过；
   这也是"只增不改"能成立的前提。
4. **输出字段只增不删。** 新增字段可以，删除或改名属于破坏性变更。
5. **拒绝要可区分。** "不支持这个能力"、"参数不合法"、"被安全规则拒绝"、"执行失败"
   必须是四种不同的、可被 Agent 分辨的结果 —— 不能让 Agent 把"被拒绝"当成"没变化"。
6. **能力集合独立于 Adapter。** 换实现方式（Native / CLI / Skill）不改变能力语义。
7. **平台无关性不得回退。** 不因为某个 Agent 平台的便利而往协议里加入平台专有概念。

---

## 10. 协议版本如何演进

协议版本使用 `主.次` 两段（当前 `1.0`），随 `agent-interface.json` 的 `version` 一同发布。

| 变更类型 | 版本动作 | 例子 |
|---|---|---|
| **破坏性变更** | 主版本 +1 | 删除 / 重命名 capability；改变已有 capability 的语义；删除输出字段 |
| **新增 capability** | 次版本 +1 | 未来真的实现了新能力，并且该能力**已经真实存在、可用、有测试** |
| **文档澄清 / 措辞修正** | 版本不变 | 表述更清楚，但语义与能力集合都没变 |

演进规则：

1. **capability 的出现与消失本身就是版本信号。** Agent 应以 `version` + `capabilities`
   两者共同判断兼容性，不能只看版本号。
2. **不声明未实现的能力。** 新 capability 必须在对应功能**真的可用之后**才写入清单 ——
   清单是"现在能做什么"，不是"打算做什么"。
3. **废弃要给过渡期。** 需要移除某 capability 时，先标记废弃（保留、标注），
   下一个主版本才真正删除。
4. **主版本变更必须同步更新本文件与 `agent-interface.json`**，两者不允许不一致。

---

## 11. 落地记录（现状观察 + 已兑现部分）

以下是与"未来 Agent 接口"相关的现状观察，最初**只记录、不顺手修改**。
最小 CLI（第 13 节）落地后，逐条复核如下（未改动的仍如实标注）：

1. **入口没有历史包袱。** `LastRegret.exe` 至今**完全不解析命令行参数**
   （`App.xaml.cs` 中没有读取 `e.Args`）。CLI 因此走了独立可执行文件，
   不存在"改动现有参数"的风险。（仍然成立）
2. **引擎可直接复用，不需要重写。** ——**已兑现**：CLI 宿主直接调用
   `AppRuntime.Create()` / `Dispose()`，自身只做参数解析与 JSON 编解码，
   `LastRegret.Agent` 工程以 `net8.0` 为目标、只引用 `LastRegret.Runtime`，
   编译期就保证它不认识 WPF。
   （`App.DisposeRuntime` 那个幂等包装只是 WPF 壳自用的，CLI 宿主用不到。）
3. **安全机制现成。** ——**已兑现**：`restore` 要求传入预览指纹、执行前必然建立安全点，
   CLI **没有新造任何安全机制**，只是"不绕过"。
4. **一处与本协议无关的既有不一致（仍仅记录）：** `code/Directory.Build.props` 里的
   `Product` 仍是旧品牌「最后悔的 Ctrl+Z」，被 `LastRegret.App.csproj` 的
   `回溯 (TimeBack)` 覆盖。不影响构建产物，也不属于本协议范围，**未改动**。

---

## 12. 一句话总结

**Agent 侧现在有一个最小可用的 CLI 入口：一次调用、JSON 进、JSON 出，且完全复用现有的恢复安全机制；**
**GUI 用户视角依然无感知，协议版本仍是 1.0，能力集合没有增加。**

---

## 13. 最小 CLI 用法（已实现）

**定位：最小可用，不是完整 Adapter。** 可以简陋，但不可以危险 ——
不监听端口、不做常驻服务、不新增能力、不提供绕过预览或忽略指纹的任何开关。

### 13.1 调用形态

```
LastRegret.Agent.exe <command> [<request.json>]
```

- `<command>`：能力 id（`protected-folders` / `timeline` / `changes` / `preview-restore` /
  `restore` / `undo`）或 `capabilities` / `help`（`help` 返回用法说明）。
  **命令只认 argv**，stdin 只承载参数（stdin 里出现的 `capability` 字段不会被使用）。
- 请求体**只从 stdin 读**（一个 JSON 对象）：`echo '{...}' | LastRegret.Agent.exe timeline`。
  管道里没有内容时按"空请求"处理。全部字段可选（`rootId` / `atUtc` / `limit` / `fingerprint` /
  `allowNewRemovals` / `operationId` 等），缺什么由对应命令自行校验；
  无参数的命令（如 `capabilities`）可以完全不给 stdin。
- 进程内一次性调用：**一次调用只处理一个请求**，处理完即退出。

### 13.2 输出约定

- **stdout 恰好是一个 JSON 对象**，没有日志、没有前后缀，可被任何 JSON 解析器直接解析。
- 诊断信息一律走 **stderr**（例如 JSON 语法错误的细节），stdout 保持干净。
- 退出码：`0` 成功或无变化，`1` 参数/命令/JSON 不合法，`2` 被安全规则拒绝，`3` 执行失败。
- `error` 与 `warnings` **在空的时候不出现**（不是 `null`，也不是 `[]`）。

成功（真实输出，已省略部分字段）：

```json
{ "protocol": "timeback-agent", "version": "1.0", "capability": "restore.preview",
  "ok": true, "status": "success",
  "data": { "fingerprint": "3DC82391F3216E38B7A493DB6364D839", "hasEffect": true,
            "stepCount": 1, "toRestore": 1, "toRemove": 0, "affected": 1, "warnings": [] } }
```

被拒绝（退出码 2，**不会伪装成成功**）：

```json
{ "protocol": "timeback-agent", "version": "1.0", "capability": "restore.execute",
  "ok": false, "status": "rejected",
  "error": { "code": "fingerprint_mismatch",
             "message": "恢复计划已经变化，请重新预览后再执行。" } }
```

`status` 有四种：`success` / `no_change` / `rejected` / `failed` ——
`ok` 与 `status` 必须一致（拒绝与失败绝不可能是 `ok: true`），
`error.code` 是可分辨的机器可读代码（`unknown_command` / `invalid_request` /
`missing_parameter` / `root_not_found` / `fingerprint_required` / `fingerprint_mismatch` /
`undo_unavailable` / `internal_error` 等）。

### 13.3 示例

探查能力清单（不需要初始化引擎，无副作用）：

```
LastRegret.Agent.exe capabilities
```

只读预览（不改动任何文件，可反复调用）：

```
echo {"rootId":1,"atUtc":"2026-09-14T02:12:10.0466796Z"} | LastRegret.Agent.exe preview-restore
```

确认执行（**必须带上预览返回的 `fingerprint`**）：

```
echo {"rootId":1,"atUtc":"...","fingerprint":"3DC8...D839","allowNewRemovals":false} | LastRegret.Agent.exe restore
```

撤销：不带 `fingerprint` 时等同于"预览能否撤销"（返回指纹），带上时才真正执行：

```
echo {"rootId":1,"operationId":1} | LastRegret.Agent.exe undo
```

### 13.4 边界（重要）

- **`restore` 不接受完整恢复计划。** 恢复计划是引擎内部结构，不上线到接口上；
  CLI 只接收"根目录 + 时间点"，**自己重新推导一次计划，仅用于比对调用方给的指纹**。
  指纹不一致会在**任何写入之前**以 `fingerprint_mismatch` 拒绝 ——
  即"确认的必须是同一份预览"这条规则在 CLI 上同样成立。
- **`undo` 不新增能力。** 它复用 `restore.undo`：无指纹=预览，有指纹=执行。
- **`includePaths` 里的目录路径表示"它以及它下面的整棵子树"。**
  勾选一个文件夹就是要恢复文件夹里的内容，而不是只恢复文件夹这个条目本身；
  勾选一个文件仍然只影响那一个路径。
- **没有 `force` / `skip-preview` 之类的开关**，也不打算加。
- **`capabilities` 的清单与 `agent-interface.json` 保持一致**（由测试强制校验），
  协议版本与能力集合在本轮**均未变更**。
