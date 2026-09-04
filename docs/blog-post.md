# 把 99 元的小米电视遥控器变成 Windows 无线麦克风 —— MiVoiceMic 技术解析

> 项目：[MiVoiceMic](https://github.com/&lt;你的用户名&gt;/mi_mic)（GPL-3.0 开源）
> 一句话：按住小米蓝牙遥控器 2 Pro 的语音键说话，松开即完成输入——配合微信输入法或 Windows 语音键入（Win+H），在任何输入框做语音输入，人不用坐在电脑前。
> 前置说明：本项目的 ATVV 协议逆向与内核驱动来自 [QL-4/RemoteMapper](https://github.com/QL-4/RemoteMapper)，界面与交互参考 [HD838A/remote-mic-app](https://github.com/HD838A/remote-mic-app)（macOS），本文站在两位前人的肩膀上，讲 Windows 端完整实现的全部技术细节。

## 一、来龙去脉

小米蓝牙遥控器 2 Pro（RC003，约 ¥99）是小米电视的语音遥控器：按住语音键说话，电视就能识别。它的语音能力走的是小米私有的 **ATVV 协议**——按住语音键时，遥控器通过 BLE 持续把 ADPCM 压缩语音推给电视端。

把它接到 Windows 上会发生什么？系统只把它认成一个普通 BLE 键盘：方向键、确定键可用；语音键被映射成 F5，按下去疯狂刷屏；而返回、主页、菜单、直播、电源、音量± 这 7 个键**彻底消失**（后文细讲为什么）。

想让语音键在 Windows 上工作，需要替电视端完成三件事：用 ATVV 协议"开麦"并接收语音流、把语音变成系统能用的"麦克风"、替用户触发一次语音识别。前两件事此前在 Windows 上只有 RemoteMapper 的逆向笔记和内核驱动，没有完整可用实现；第三件事则有一个关键的架构决策——**不做语音识别**。Windows 上的输入法（微信输入法、Win+H 语音键入）早已把"按住说话、松开上字"打磨得很好，MiVoiceMic 要做的只是：把遥控器的声音喂给系统，然后替用户按下输入法的语音热键。

于是就有了这条链路。

## 二、总体架构：一条语音的完整旅程

```
遥控器(按住语音键) ──BLE·ATVV──▶ MiVoiceMic
                                   │ ① GATT 订阅通知，收到 120 字节/帧的 ADPCM
                                   │ ② IMA ADPCM 解码 (16 kHz) + 去尖峰/低通/AGC
                                   │ ③ waveOut 推流到虚拟声卡 "CABLE Input"
                                   │ ④ IPolicyConfig 把默认麦克风临时切到 "CABLE Output"
                                   │ ⑤ SendInput 注入输入法语音热键（按住期间保持）
                                   ▼
                          微信输入法 / Win+H 识别 ──▶ 文字上屏
                                   │ 松开语音键
                                   ▼
                          释放热键 → 恢复原默认麦克风 → 统计落盘
```

代码组织上，每个环节一个模块，全部在 `src/` 下：`BleVoiceLink`（WinRT BLE 与 ATVV 状态机）、`AdpcmDecoder`（解码与音频修复）、`AudioOut`（waveOut 推流与 WAV 落盘）、`DeviceSwitcher`（COM 切默认麦克风）、`HotkeyInjector`（SendInput 注入）、`RemoteKeys`（低级钩子 + Raw Input 归因 + 手势引擎）、`KeyMap`（映射数据模型）、`App`（编排器，持有 keyworker 与 decode 两个工作线程）、`Config`/`Log`、以及 WinForms UI 四页。线程间用 `BlockingCollection` 队列衔接，UI 通过带版本号的 `UiState` 快照解耦 BLE/解码线程——蓝牙回调来自线程池线程，绝不直接碰 UI。

构建产物是单个约 189 KB 的 `MiVoiceMic.exe`，这个后面细说。

## 三、ATVV：小米遥控器的语音协议

ATVV 服务与特征（与 RemoteMapper 的逆向笔记、remote-mic-app 的 Swift 实现三方交叉验证一致）：

| GATT 对象 | UUID | 用途 |
|---|---|---|
| ATVV 服务 | `ab5e0001-5a21-4f05-bc7d-af01f617b664` | 语音总入口 |
| 命令特征 `C_CMD` | `ab5e0002-…` | 主机 → 遥控器，WriteWithoutResponse |
| 音频特征 `C_AUD` | `ab5e0003-…` | 遥控器 → 主机，语音数据 Notify |
| 控制特征 `C_CTL` | `ab5e0004-…` | 双向控制信令 Notify |
| 电池服务 | `0x180F` / `0x2A19` | 读电量（UI 显示用） |

**握手时序**：找到已配对设备（按名字或 MAC 前缀 `C0:5D:39` 匹配；找不到就扫描广播并尝试自动配对）→ `MaintainConnection` 保持连接 → 订阅 `C_CTL`/`C_AUD` 的 Notify → 发 `GET_CAPS`（`{0x0A,0x01,0x00,0x00,0x03,0x03}`）→ 等 `CAPS_RESP`（4 秒超时即报"CAPS 无响应"）→ 协商结果里拿协议版本、帧大小（默认 120 字节）和编解码：**codec 2 = 16 kHz，直接要求；codec 1 = 8 kHz，拒绝握手**（见"未完成"一节）→ 发 `MIC_OPEN`。此后每 5 秒发一次 `MIC_EXTEND` 保活，保活写失败即触发重连。

**语音分帧**：120 字节 ADPCM 一帧 = 240 个 PCM 采样 = **15 ms**。BLE 通知与帧没有对齐关系（可能拆帧/并帧），解码器内部用累加器重组，见下一节。

**会话语义**：遥控器开始推流时会发 `AUDIO_START`，其中 interaction 字节标明会话来源——只有 **`0x03`（HTT，按住说话）** 才触发切麦+热键注入；其他来源的会话只出声不注入，这个区分避免了"遥控器固件自启会话导致输入法莫名弹语音条"的怪事。`AUDIO_STOP`、`MIC_CLOSED` 或 BLE 断连都会走统一的 `OnVoiceStop`：释放热键、恢复麦克风、写 WAV dump（调试开关）、累计统计。意外断连 500 ms 后重连，连续失败按 `min(3 + 2n, 15)` 秒退避。

**实现层面的坑**：项目用 .NET Framework 4.8 的 csc 直接编译，WinRT 类型只能从 `System32\WinMetadata` 的 `.winmd` 引用，于是 WinRT 事件不能 `+=`（CS1545，要反射调 `add_ValueChanged`）、部分枚举未投影（`Enum.ToObject` 硬转）、`IAsyncOperation<T>` 要手写包装成 Task。这些都封装在 `BleVoiceLink` 一个文件里，外面看到的仍是干净的异步接口。

## 四、音频：IMA ADPCM 解码与"土法"修复

解码器是标准 IMA/DVI ADPCM：4-bit nibble（高半字节在前）、89 级步长表、自适应索引表 `{-1,-1,-1,-1,2,4,6,8}`，predictor 与 stepIndex 按帧头同步。遥控器很贴心地提供了 `AUDIO_SYNC` 信令，携带大端的 predictor 和 step index——解码器收到 SYNC 会清空待定缓冲（sync 之前的半帧视为无效），并在下一帧应用同步状态，丢包后不会永远失真。

解码之后做了三级后处理，都是"听感工程"：

- **去尖峰（declip）**：当前样本与前后邻居差都超过 1000、且差值远大于邻居间距时判为孤立坏点，用邻居均值替换。蓝牙偶发坏 nibble 的典型症状就是"咔"一声，这一招基本消灭了它。
- **低通**：3 抽头 FIR `(prev + 2·cur + next) >> 2`，帧间状态跨帧维护（每帧末样本不滤波，避免边缘效应）。
- **AGC**：目标峰值 28000、地板 200、最大增益 30 倍、峰值按 0.9997 慢衰减——"瞬升慢降"让音头不吃、句间自动抬。关掉 AGC 也可用固定增益（±24 dB）。

所有解码状态在锁内——BLE 回调来自线程池线程，帧序不能乱。

## 五、三个"偏方"：虚拟声卡、切默认麦、注入热键

这三个环节没有任何一个是"正规 API 能办的事"，但都有成熟的社区偏方。

**① 推流：裸 P/Invoke waveOut。** 不引 NAudio，直接 `waveOutOpen/waveOutWrite` 一套 winmm 调用，16 kHz/16 bit/单声道 PCM 推给名字含 `CABLE Input` 的播放端。10 个 waveOut 缓冲（约 150 ms 池深）+ 40 帧（约 600 ms）入队上限，满了丢最旧并计数。VB-CABLE 可能后装，程序每 30 秒重试打开声卡（late-attach）。调试时可用 `dumpAudio` 把每次会话落一份 WAV。

**② 切默认麦克风：未公开的 IPolicyConfig。** COM 类 `CPolicyConfigClient`（CLSID `870af99c-171d-4f9e-af0d-e63df40c2bc9`），接口 IID `f8679f50-…`，vtable 前面有 10 个占位方法，之后才是 `SetDefaultEndpoint`——对同一端点依次设 `eConsole/eMultimedia/eCommunications` 三个角色。AudioSwitcher、SoundSwitch 等工具用的同款技法。切换前记住原默认端点，松开后恢复。切完到注入热键之间留 `switchLeadMs`（默认 120 ms），给输入法感知设备变化的时间——这个延迟在 UI 里可调，远程桌面等场景调到 250 ms 更稳。

**③ 注入热键：SendInput 的两个细节。** 一律用 `KEYEVENTF_SCANCODE` + `MapVirtualKey` 发扫描码，且扩展键列表必须补 `KEYEVENTF_EXTENDEDKEY`——**右 Alt 是扩展键，不加这个标志输入法会看成左 Alt**，热键就直接失灵了（微信输入法默认语音键恰好是右 Alt+逗号）。注入模式两种：hold（按住跟随）与 tap（点按开关，再点一次结束）。注入前还有个小动作：**先把自己的全局钩子卸掉**——按住语音键时遥控器连发 HID F5，而 `WH_KEYBOARD_LL` 会把全系统输入编组到钩子线程，干扰注入时序；注入完成后再投消息给 pump 线程重装。这是个自认的 workaround，窗口期理论上漏拦截，后面"未完成"一节会再提。

另有一个语义细节：注入的按键带着 `LLKHF_INJECTED` 标志，本程序自己的钩子会跳过一切注入输入——这是按键映射"不干扰物理键盘"防线的一部分。

## 六、按键映射：Raw Input 归因与三层 bug

按键映射的需求一句话就能说清：把遥控器的方向键映射成 Tab/退格/任务视图等等，**且物理键盘不受影响**。难点在归因——低级键盘钩子只能看到虚拟键，看不到"是哪个设备按的"。

方案是双通道对照：一个 message-only 窗口注册 **Raw Input**（`usUsagePage=1, usUsage=6, RIDEV_INPUTSINK`），在 `WM_INPUT` 里用 `GetRawInputData(RIDI_DEVICENAME)` 查按键来源设备路径，写入 64 项环形缓冲（键、按下/抬起、时间戳）；低级钩子这边收到可疑键（F5 或已绑定的键）时，去环形缓冲里做时间窗关联——最多 12 次轮询、每次 1 ms，超过 250 ms 的记录作废，**关联失败一律按物理键盘放行**（fail-open）。三层防线：注入输入直接跳过 → Raw Input 归因为遥控器才拦截 → 归因失败放行。设备路径匹配同时生成三种子串（USB 风格 `VID_2717&PID_32B8`、BLE 风格 `VID&012717_PID&32b8`，以及 vendorIdSource=2 的变体）加 MAC 前缀兜底，按 `hDevice` 缓存判定结果。

手势引擎（点按/长按，阈值默认 600 ms）是纯逻辑类，动作投递到专用工作线程执行——**绝不在钩子回调里做事**，这是钩子程序的基本修养。

### 三层 bug：一个"静默失效"的完整标本

这套归因在真机上曾经完全失效，而且日志里一个报错都没有。最后剥出来是三层错误叠在一起，每一层都把下一层的症状掩盖了：

1. **常量抄错**：`GetRawInputData` 查设备名应传 `RIDI_DEVICENAME = 0x20000007`，最初写成了别的值——每次查询直接失败。
2. **两段式调用的陷阱**：Win32 惯例是"先传 NULL 探所需缓冲大小，再正式调用"，但对 `RIDI_DEVICENAME` 这个用法，**传 NULL 直接失败**，必须一次性传入足够大的缓冲。两行错在同一个函数里。
3. **路径格式想当然**：就算查询成功了，最初用 USB 风格的 `VID_…&PID_…` 子串去匹配，而 **HID-over-GATT 设备的路径长着完全不同的样子**（`HID\{1812…}_DEV_VID&012717_PID&32b8_REV&…_MAC`），照样匹配不上——归因被静默禁用。

最终症状是"映射在真机上没反应"，无崩溃、无日志。教训：对这类多层管线，**每一层都要有独立的可观测性**（本项目后来加了归因失败日志），调试时沿数据流逐层做最小验证，而不是只盯最终现象。三处修复都以注释形式留在了 `RemoteKeys.cs` 里。

## 七、七个"消失"的按键与内核过滤驱动

遥控器上 7 个键——返回、主页、菜单、直播、电源、音量±——在 Windows 上不是"映射错了"，而是**信号在内核层被丢弃**。它们发的是非标准 Keyboard Page HID 用法，键盘类驱动 `kbdhid.sys` 找不到对应的扫描码/VK，直接丢弃。丢弃点在内核意味着：全局钩子收不到、Raw Input 收不到、`WM_APPCOMMAND` 无事件、PowerToys/AutoHotkey/注册表 Scancode Map 这些"重映射已存在按键"的工具无键可映射；WebHID 和 GATT 直读也走不通（Keyboard Page 受保护、HID 服务被系统独占）。完整分析（含每条替代路线的实测结论）见 [docs/NON-STANDARD-KEYS.md](NON-STANDARD-KEYS.md)。

唯一可行的软件路线是 RemoteMapper 实现的 **KMDF 内核过滤驱动**：在报告到达 `kbdhid.sys` 之前，把这 7 个键的 usage 等长改写成 F13–F19。驱动精确绑定 `VID 2717 / PID 32B8` 这一个设备的键盘 TLC，ATVV 语音走厂商私有服务、不经过该 TLC，因此互不影响。MiVoiceMic 内置了该驱动（同为 GPL-3.0），按键映射页可一键调起安装脚本。

代价必须说清楚：测试签名驱动与 **Secure Boot 互斥**，安装需要关 Secure Boot、装证书、重启两次，且 BitLocker 用户要先备份恢复密钥。转正之路是把它提交微软做证明签名（attestation signing）——需要 EV 证书和微软硬件开发者计划，个人项目暂时做不到，这是本项目最大的遗憾（详见最后一节）。

## 八、工程化：零 SDK 构建、自测与 E2E

**构建**：`build.bat` 调用 Windows 自带的 .NET Framework 4.8 编译器（`Framework64\v4.0.30319\csc.exe`），不装任何 SDK。WinRT 类型直接引用 `System32\WinMetadata` 下的 `.winmd`，JSON 用 `JavaScriptSerializer`，产物是带图标的单文件 x64 exe（约 189 KB）。克隆下来双击 `build.bat` 就能出包，这是刻意的取舍——降低"想改两行试试"的人的门槛。

**测试**：三层。

- `--selftest`：31 项离线断言，覆盖解码器 round-trip（440 Hz 正弦编码再解码、跳过冷启动瞬态后最大误差 < 1500）、分帧重组、配置解析、热键键名、手势引擎阈值、映射数据规范化。
- `--e2e`：**无遥控器的全链路自测**——用系统 TTS 合成一段 16 kHz 中文语音，推完整条"解码 → CABLE → 切默认麦克风 → 恢复"管线，出 PASS 才算环境就绪；加 `--inject` 还会真注入一次热键。
- `--check`：环境四件套逐项检查（蓝牙适配器、遥控器配对、VB-CABLE 两端、微信输入法）。

这套"自检 + 自测 + E2E"是跟真机联调磨出来的：真机问题永远要先排除环境问题，`--check` 全绿再 `--e2e` PASS，剩下的才值得怀疑遥控器。另有 `--sniff`（键盘嗅探，确认按键到达的虚拟键码）和 `--screenshot`（离屏渲染 UI 成 PNG，不启动蓝牙和钩子）。

## 九、还没做完的事

按诚实清单的惯例，全部列出来：

- **7 键驱动的门槛**：Secure Boot 开启的电脑装不了测试签名驱动，那 7 个键不可用（语音输入不受影响）。正解是证明签名，需要 EV 证书 + 微软硬件开发者计划——欢迎有条件的组织接手这一步。
- **8 kHz 不支持**：CAPS 协商到 codec 1（8 kHz）直接判握手失败，没做重采样；只有 16 kHz 固件的设备能用。
- **F5 误伤**：连接期间物理键盘的 F5 也会被吞（历史行为，UI 有开关）。要做到"物理 F5 放行"需要把 F5 也纳入 Raw Input 归因，是已明确的待办。
- **注入期间卸钩子**：防止 F5 连发干扰钩子线程的 workaround，窗口期理论上漏拦截；更干净的做法还在想。
- **归因是启发式**：毫秒级时间窗关联，极端调度延迟下可能漏判（fail-open，不误伤）。
- **未公开接口风险**：IPolicyConfig 属 undocumented API，Windows 更新理论上可能失效（社区工具多年验证它相当稳定）。
- **固件决定的天花板**：只有"按住说话"，无持续录音；微信输入法要求输入框焦点，无焦点时识别结果不上屏。
- **静默丢帧**：音频队列满时丢帧只计数不告警；低通滤波每帧末样本不参与——都属于已知的小瑕疵。

## 十、致谢与链接

- [QL-4/RemoteMapper](https://github.com/QL-4/RemoteMapper)（GPL-3.0）：ATVV 协议逆向、`MiRemoteHidFilter` 内核驱动、非标准按键的完整分析。
- [HD838A/remote-mic-app](https://github.com/HD838A/remote-mic-app)（GPL-3.0）：macOS 端原版应用，本项目界面与交互的参考。
- [VB-CABLE](https://vb-audio.com/Cable/)：免费的虚拟声卡，整个方案的基石之一。

项目代码以 GPL-3.0 发布，仓库地址见文首（发布前把 `&lt;你的用户名&gt;` 占位符替换为实际仓库地址）。非官方项目，与小米公司无关。
