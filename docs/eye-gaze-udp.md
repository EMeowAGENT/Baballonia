# Baballonia → WiVRn Eye Gaze UDP 协议

版本：1（`version = 1`）

本文是 **Baballonia 发送方** 与 **WiVRn 接收方** 之间的 eye gaze 数据协议规范。接收方按本文即可独立实现，不需要阅读 Baballonia 源码。

---

## 1. 传输层

| 项目 | 值 |
| --- | --- |
| 传输 | UDP |
| 地址 | loopback `127.0.0.1` |
| 默认端口 | `9010`（接收方可配置） |
| 字节序 | 小端 little-endian |
| 包长 | 固定 **64 字节**，不接受变长 |
| 发送频率 | 每个新眼样本最多发一次，上限 **90 Hz** |
| 时间戳时钟 | `CLOCK_MONOTONIC` 纳秒（u64 LE） |

所有整型/浮点均为定长、显式小端；不依赖 C 结构体内存布局的对齐填充。

---

## 2. 64 字节布局

| 偏移 | 长度 | 类型 | 字段 | 说明 |
| --- | --- | --- | --- | --- |
| 0..3 | 4 | bytes | `magic` | ASCII `"WVGZ"` = `57 56 47 5A` |
| 4 | 1 | u8 | `version` | 固定 `1` |
| 5 | 1 | u8 | `flags` | bit0 = combined valid，bit1 = left valid，bit2 = right valid；其余位为 0 |
| 6..7 | 2 | u16 | `reserved` | 发送方填 0，接收方忽略 |
| 8..15 | 8 | u64 LE | `timestamp_ns` | `CLOCK_MONOTONIC` 纳秒 |
| 16..19 | 4 | f32 LE | `qx` | combined 四元数 x |
| 20..23 | 4 | f32 LE | `qy` | combined 四元数 y |
| 24..27 | 4 | f32 LE | `qz` | combined 四元数 z |
| 28..31 | 4 | f32 LE | `qw` | combined 四元数 w |
| 32..35 | 4 | f32 LE | `left_yaw_deg` | 左眼 yaw，度 |
| 36..39 | 4 | f32 LE | `left_pitch_deg` | 左眼 pitch，度 |
| 40..43 | 4 | f32 LE | `right_yaw_deg` | 右眼 yaw，度 |
| 44..47 | 4 | f32 LE | `right_pitch_deg` | 右眼 pitch，度 |
| 48..51 | 4 | f32 LE | `left_lid` | 左眼睑开合，`0` 闭 .. `1` 开 |
| 52..55 | 4 | f32 LE | `right_lid` | 右眼睑开合，`0` 闭 .. `1` 开 |
| 56..63 | 8 | bytes | `reserved` | 发送方填 0，接收方忽略（预留扩展） |

参考结构（仅表达字段顺序，实际收发必须按偏移逐字段序列化）：

```c
struct wvrg_eye_gaze_packet {
    char     magic[4];        // "WVGZ"
    uint8_t  version;         // 1
    uint8_t  flags;           // bit0 combined, bit1 left, bit2 right
    uint16_t reserved0;       // 0
    uint64_t timestamp_ns;    // CLOCK_MONOTONIC, ns
    float    qx, qy, qz, qw;  // combined, view/HMD-relative
    float    left_yaw_deg, left_pitch_deg;
    float    right_yaw_deg, right_pitch_deg;
    float    left_lid, right_lid;   // 0 closed .. 1 open
    uint8_t  reserved1[8];    // 0
};  // 共 64 字节
```

> 注意：上面的 C 结构在常见 ABI 下恰好是 64 字节且无额外填充，但接收方仍应按上表偏移解析，不要 `sizeof` 后直接 `recv` 进未序列化的结构体。

---

## 3. 语义

### 3.1 四元数是权威值

- `qx, qy, qz, qw` 是 **combined** 朝向，表示 view / HMD 相对方向。
- 接收方应直接把该四元数填入 `xrt_pose.orientation`，**不要**用角度字段重新构造四元数。
- 角度字段（`left_* / right_*`）只用于日志、调试，以及未来实现 per-eye 姿态；当前不作为权威姿态。

### 3.2 角度与四元数约定

- 角度字段采用 **右视 yaw > 0、仰视 pitch > 0** 的语义（便于日志/调试）。
- `orientation` 四元数使用 **OpenXR 右手系**：`+X` 右、`+Y` 上、`-Z` 前。
- 绕 `+Y` 的正旋转会把前方转向左侧，因此右视 yaw 必须取负：
  - `q_yaw(-yaw) = [0, sin(-yaw/2), 0, cos(-yaw/2)]`
  - `q_pitch(pitch) = [sin(pitch/2), 0, 0, cos(pitch/2)]`
  - `q = q_yaw(-yaw) * q_pitch(pitch)`（**先 yaw 后 pitch**）
- 数组顺序为 `[x, y, z, w]`。
- 因此“看右”时 `qy < 0`；“看上”时 `qx > 0`。
- `combined` 的 yaw / pitch 取左右眼平均：
  - `combined_yaw   = (left_yaw   + right_yaw)   / 2`
  - `combined_pitch = (left_pitch + right_pitch) / 2`

### 3.3 有效性标志

- `flags`：
  - bit0：combined 有效
  - bit1：左眼有效
  - bit2：右眼有效
- 闭眼 / 无有效数据时，对应 valid bit 清零，**但四元数仍填最后一次有效值**（保持姿态连续，避免跳变）。
- 建议：平均 lid `< 0.1` 视为无效。
- 建议发送方在双眼都无效时清 bit0；单眼无效时只清对应眼位 bit。

### 3.4 时间戳

- `timestamp_ns` 为发送端 `CLOCK_MONOTONIC` 纳秒。
- 接收方应与本机 `os_monotonic_get_ns()` 比较：
  - 偏差在 **±1 s** 内：可直接使用包内时间戳。
  - 偏差超过 ±1 s（跨时钟域 / 挂起恢复）：改用 **接收时间**，避免时间轴跳变。

---

## 4. Baballonia 侧输出（已处理值）

Baballonia 发布的是 **已处理、已稳定化** 的眼动数据，与 OSC/VRCFT 路径同源，不是原始模型输出：

- 已完成左右眼遮挡修正（单眼闭合时使用另一只眼）
- 已应用 `StabilizeEyes` 的收敛/平均
- 已完成 lid 语义转换：`0` = 闭，`1` = 开
- 角度单位是 **度**，范围约 `-45°..+45°`
- `combined` 值由发送端直接给出，接收端不需要再平均
- 包里的 `left_*` / `right_*` 是**物理左/右眼**，与 VRChat 的
  `LeftEyeX/RightEyeX` 语义对齐；接收端不要再做左右交换

因此接收端 **不要** 再做 `(v - 0.5) * 90` 或 `1 - lid` 之类的换算；直接使用包内字段，并按 3.2 构造/使用四元数。

---

## 5. 接收方 checklist（WiVRn）

- [ ] 绑定 `127.0.0.1:9010`（端口可配置），**只绑定 loopback**。
- [ ] 校验 `magic == "WVGZ"`、`version == 1`、实际读取长度 `== 64`；任一项不符则丢弃该包（可计数/限频告警）。
- [ ] 用包内 `orientation`、`flags`、`timestamp_ns` 调用 pose_list 的外部样本注入接口。
- [ ] 外部源启用时，**不要**向头显请求 `EYE_GAZE`（避免双源冲突）。
- [ ] 设备 creation 条件需要覆盖 external source：即使头显不声明 eye gaze 能力，只要配置了外部源也应创建 eye gaze 设备/姿态通道。
- [ ] 时间戳按 3.4 节做 ±1 s 校验。
- [ ] 对 `flags` 的 valid bit 做处理：无效时不要用该样本更新姿态，但保留最后有效姿态。
- [ ] `reserved` 字段一律忽略，不因非零值拒包。
- [ ] 建议对接收频率做上限保护（发送端上限 90 Hz）。

---

## 6. 安全

- 只绑定 loopback（`127.0.0.1`），不接受局域网 / 公网来源。
- 不提供认证与加密：协议假定同机进程可信；不要把它暴露到非 loopback 接口。
- 接收方应校验包长与 magic，避免把任意 UDP 流量当作姿态输入。

---

## 7. 版本演进

- `magic`、`version`、`flags`、两段 `reserved` 均为**保留字段**，不得复用为其他含义。
- 向后兼容的扩展优先使用 `reserved`（bit / 字节），并保持包长 64 字节不变。
- 需要改变包长或字段语义时，提升 `version`；接收方对不认识的 `version` 应丢弃而不是猜测。
- 发送方对当前版本必须把 `reserved` 填 0；接收方必须忽略其内容。

---

## 8. 示例包（64 字节 hex dump）

示例取值（仅用于说明字段位置，数值自拟）：

- `timestamp_ns = 1_000_000_000`（开机后 1 s）
- `flags = 0x07`（combined / left / right 全部有效）
- 左眼 `yaw=8°, pitch=4°`，右眼 `yaw=12°, pitch=6°`，`lid` 左 `0.8` 右 `0.6`
- combined `yaw=10°, pitch=5°` → `q = q_yaw(-10°) * q_pitch(5°) = (0.043453, -0.087073, 0.003802, 0.995247)`

```
00: 57 56 47 5A 01 07 00 00 00 CA 9A 3B 00 00 00 00
10: 32 FC 31 3D 38 53 B2 BD 9C 25 79 3B 7A C8 7E 3F
20: 00 00 00 41 00 00 80 40 00 00 40 41 00 00 C0 40
30: CD CC 4C 3F 9A 99 19 3F 00 00 00 00 00 00 00 00
```

逐字段解读：

| 偏移 | 字节 | 字段 | 解码值 |
| --- | --- | --- | --- |
| 0..3 | `57 56 47 5A` | magic | `"WVGZ"` |
| 4 | `01` | version | `1` |
| 5 | `07` | flags | combined + left + right 有效 |
| 6..7 | `00 00` | reserved | `0` |
| 8..15 | `00 CA 9A 3B 00 00 00 00` | timestamp_ns | `1_000_000_000` |
| 16..19 | `32 FC 31 3D` | qx | `0.043453` |
| 20..23 | `38 53 B2 BD` | qy | `-0.087073` |
| 24..27 | `9C 25 79 3B` | qz | `0.003802` |
| 28..31 | `7A C8 7E 3F` | qw | `0.995247` |
| 32..35 | `00 00 00 41` | left_yaw_deg | `8.0` |
| 36..39 | `00 00 80 40` | left_pitch_deg | `4.0` |
| 40..43 | `00 00 40 41` | right_yaw_deg | `12.0` |
| 44..47 | `00 00 C0 40` | right_pitch_deg | `6.0` |
| 48..51 | `CD CC 4C 3F` | left_lid | `0.8` |
| 52..55 | `9A 99 19 3F` | right_lid | `0.6` |
| 56..63 | `00 00 00 00 00 00 00 00` | reserved | `0` |

> 四元数已归一化（模长 1.0）；发送方应始终发送归一化四元数，接收方可选择对异常值做归一化或丢弃。
