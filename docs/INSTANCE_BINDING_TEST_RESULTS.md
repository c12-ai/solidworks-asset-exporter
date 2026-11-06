# 实例承载关系候选版验证记录

本页保留 rc1 验证记录；2026-11-03 的 rc2 交互改进及新增测试见[交互改进记录](INSTANCE_BINDING_INTERACTION_RESULTS.md)。

日期：2026-11-02（Asia/Shanghai）。版本：1.1.0-rc1；Core/Add-in 文件版本：1.1.0.0。

代码、导出适配、105 项自动回归、真实 Interop 构建和 SW2025 SP5.0 实机验证通过，已生成候选安装包。下表记录工程验证覆盖，不代替用户正式验收签字；消费方导入联调、真实 NAS 上传和 P1 工作流没有完成。

## 环境与交付

| 项目 | 记录 |
| --- | --- |
| SOLIDWORKS | 2025 SP5.0，API RevisionNumber `33.5.0`，官方 Interop `33.5.0.53` |
| Windows | NT 10.0.26200.9711 / DisplayVersion 25H2；注册表 ProductName 返回 Windows 10 Home China，保留原始记录 |
| 用户指定模型 | `C:\Users\C12\Desktop\（部分）layout_all\（部分）layout_all 20261028.SLDASM` |
| 原模型检查 | 354 个导出节点、277 个普通 Asset；12 个嵌套实例持久引用往返成功；未写入测试关系 |
| 基础实机副本 | `artifacts/binding-fixture-20261102/Binding-S1.SLDASM`，两个重复普通子装配，每组两个架子和三根试管 |
| 扩展实机副本 | `artifacts/binding-extended-20261102/Binding-AT-S1.SLDASM`，用于状态、配置、删除/替换、改名、位姿与预览失效检查 |
| 当前用户安装 | `C:\Users\C12\AppData\Local\SolidWorksAssetExporter\Addin`；实际 GetBuildInfo 返回该目录及 `instance-bindings-v1` |
| 最终 Add-in SHA-256 | `B2C7D8C5051155985103D681EA7516160C3559C68E7E607F0711D982EE8667CD`，与生产 Release 输出相同 |
| 系统安装备份 | `artifacts/installation-backup-20261102`；系统安装目录 ACL 不允许写入，原系统 DLL 未被替换 |
| 本次升级回退 | `%LOCALAPPDATA%\SolidWorksAssetExporter\UpdateBackups\20261102-202340\com-codebases.json` 回到上一候选版；`20261102-191050` 备份可回到最初系统 CodeBase |
| 安装包 | `artifacts/SolidWorksAssetExporter-v1.1.0-rc1.zip`，含五个真实生产 DLL、安装/回退脚本、协议、Schema、样例和证据 |

原始文件仅用于读取和复制测试源；测试修改、保存、替换、删除均在工作区副本中完成。早期测试会话中一个原始引用装配被 SW 加载/重建标为 dirty，退出前另存了独立恢复副本，未保存原文件。恢复副本位于 `artifacts/session-recovery-20261102-194531`，见 `session-recovery.log`。最终已重新打开用户指定总装配，所有已打开文档均记录为 dirty=False，见 `final-documents.log`。

## 证据索引

日志及原生控件记录位于 [artifacts/instance-binding-evidence](../artifacts/instance-binding-evidence)，随候选包一并交付。

| 文件 | 内容 |
| --- | --- |
| tests.log | 105 项全部通过，其中新增 22 项绑定专项；保留原回归测试 |
| contract.log / production.log | 隔离测试桩契约、真实 Interop Release 构建通过；桩 DLL 未安装/打包 |
| live-model.log | 指定总装配扫描及持久引用往返 |
| live-fixture.log / live-export.log | 基础 S1 保存/重开及真实生产导出；首次模拟注册表缺少协议头被阻止，修正后完整导出成功 |
| live-extended.log | 原子冲突、环、接口/角色、隐藏/抑制/包络、失败保存/重试、双配置、旧预览和版本升级、完整几何/源模型导出 |
| live-mutations.log | 新增实例、删除后同源文件重插、真实 ReplaceComponents2、失效保留及显式重绑 |
| live-proximity.log | 靠近 B 不改变归属、移动子只改变相对位姿、跨 CAD parent 多层链、复制配置空图、实际空关系文件 |
| live-rename.log | 实际 Name2 改名、重建、保存/关闭/重开后全部关系仍指向原实例 |
| native-*.txt | 正式 Add-in 多选清单、去重/移除、空选择、多父拒绝、冲突、换父确认、定向解绑、无效元数据恢复界面 |
| highlight.log / ui-selection.log / ui-save.log | 原生高亮后真实 SelectionMgr 两端、三项选择、原生面板操作后保存清除 pending |
| invalid-plan.log / invalid-plan-restored.log | 根端点拒绝，临时无效元数据与恢复；恢复后三条关系有效 |
| addin-load.log / user-install.log / binary-hashes.log | 最终实际加载、用户升级和 DLL 哈希 |
| original-restored.log / final-documents.log | 用户指定总装配恢复为活动文档及最终保存状态 |

构建环境缺少 .NET Framework 4.8 targeting pack，MSBuild 使用本机运行时/GAC 引用，输出 MSB3644/MSB3270 警告。生产 DLL 已在目标 SW 中实际加载运行；尚未补充独立干净 SDK 构建环境验证。

## 原验收项的工程覆盖

“自动＋实机”指规则由自动回归覆盖，同时使用真实 SW API 和/或正式原生面板检查相应链路。没有把每个规则都称作逐按钮人工验收。正式验收人可按原表复核下列证据。

| ID | 工程覆盖 | 结果与证据 |
| --- | --- | --- |
| AT-01 | 自动＋实机 | 正式菜单、无文档提示、可写装配；实际零件/只读写入拒绝，保存图未变 |
| AT-02 | 自动＋原生界面 | 单父、三子读取与重复读取去重、移除/清空未应用项、空选择/多父拒绝、名称/路径/计数；三项选择由真实 Select4 准备，面板从 SelectionMgr 读取，未把未成功的键盘多选算作证据 |
| AT-03 | 自动＋实机 | 根/group/meshes/workstation/robot 禁止；实际根、group、robot 及 Asset 内部不可选边界；meshes/workstation 独立规则由自动测试覆盖 |
| AT-04 | 自动＋实机 | A/B 同资产不同实例，仅 A→T1/T2/T3；保存、重开及样例包 |
| AT-05 | 原生界面＋实机 | 关系恢复、未绑定提示、逐关系高亮；SelectionMgr 实际选中恰好父/子两端 |
| AT-06 | 自动＋原生界面 | 幂等应用不产生 dirty；解除一条保留其余两条，重新应用恢复三条 |
| AT-07 | 自动＋原生界面 | 普通应用拒绝整批冲突且无部分修改；换父确认逐项旧 A→新 B，应用后再显式恢复 A |
| AT-08 | 自动＋实机 | 未应用选择不保存；实际已应用未保存关系关闭而不保存，重开恢复原保存图 |
| AT-09 | 自动＋实机 | 自身和长环拒绝，拒绝后原图不变 |
| AT-10 | 自动＋实机 | 真实跨不同 CAD parent 多层承载链成立，CAD 层级不变 |
| AT-11 | 自动＋实机 | 已绑定后改为缺失/不匹配接口或禁止角色链均阻断；父自身 connection_interface 可空 |
| AT-12 | 自动＋实机 | 实际将 B 移近子对象不改变声明归属；移动子只改变 A 相对位姿；实现不读取配合作归属推断，未另建完整配合组合模型 |
| AT-13 | 自动＋实机 | 未绑定 B 和第二组对象继续导出，无 slots_num/空槽位推断 |
| AT-14 | 自动＋实机 | 保存、关闭、重开后持久引用解析原实例，不依赖旧 RCW |
| AT-15 | 自动＋实机 | 绑定/解绑置 dirty；受控真实保存失败保留 pending，重试成功；取消/另存副本隔离测试不误清状态 |
| AT-16 | 自动＋实机 | 实际改名与重建/重开后仍为原 occurrence；最终恢复名称基部，SW 自动分配了新显示后缀，没有假称完整旧 Name2 不变 |
| AT-17 | 自动＋实机 | A、B 配置不同父图，切换及重开分别保留；真实新建/派生复制配置初始空图 |
| AT-18 | 自动＋实机 | 删除后失效记录保留；同名源文件/同 Asset 重插不修复；SW 给新 occurrence 分配新后缀；真实替换被源身份保护阻断，仅显式解除/重绑修复 |
| AT-19 | 自动＋实机 | 真实隐藏、抑制、包络、失去独立 Asset 属性均阻断并保留；恢复后可用；元数据阻止整棵计划构建时，正式面板仍显示三条可解除记录 |
| AT-20 | 自动＋实机 | 重复子装配上下文独立，新插入的重复实例没有继承绑定 |
| AT-21（P1） | 未完整执行 | 配置改名依赖稳定配置 ID，源路径变化按保护规则要求重绑；Save As / Pack and Go / PDM 组合未专项验收 |
| AT-22 | 自动＋实机 | 真实包端点唯一、UUID/version 与 XML/report 一致 |
| AT-23 | 自动＋实机 | 同组 S2 位移 (0.1,0.2,0.3)m；实际不同嵌套组位移与旋转符合计算 |
| AT-24 | 自动＋实机 | 实际相对 +90° 四元数，以及共同外层旋转/平移下父相对位姿不变 |
| AT-25 | 自动＋实机 | 有/无绑定 XML 字节一致自动测试；真实导出保留 CAD parent/pose 与 Asset 硬边界 |
| AT-26 | 自动＋实机 | 实际空配置输出 relations=[]；旧包缺文件仅表示无显式信息，不回退 group 推断 |
| AT-27 | 自动 | 删除、篡改及重算哈希后同包身份不一致均拒绝 |
| AT-28 | 自动＋实机 | 新格式和绑定/换父/解绑/位姿进入指纹；实际已有版本拒绝改图，提升版本后接受 |
| AT-29 | 自动＋实机 | pending 修改和保存后的 revision 修改分别拒绝旧预览；business dirty 优先于内部解析豁免 |
| AT-30 | 自动 | 模拟 HTTP ZIP 含逐字一致的关系和 report，上传前完整校验；未执行真实 NAS 上传，不宣称远端原子发布已验收 |
| AT-31 | 自动＋实机 | 105 回归、契约、真实构建、已有安装的每用户升级、正式加载/菜单及生产导出；新机器首次管理员安装/卸载未重复执行 |
| AT-32 | 交付可检查 | 候选 ZIP、说明、Schema、原始真实样例、回退脚本及证据齐备；消费方契约确认和正式验收签字待完成 |

已修复并实机复核：动态 COM 存储调用报无效索引（改为强类型 Interop）；面板激活扫描夺取焦点（SW owner 和轻量状态定时器）；外部 COM 回调创建无消息循环窗口（调度到 SW UI 线程）；无效元数据导致无法列出旧关系（失败计划下保留关系行）。

## 数值和包验证

基础 S1 的 S2 为 `tx=0.10000000000000009, ty=0.19999999999999996, tz=0.3, q=(0,0,0,1)`。跨组实测 `tx=0.24576276125297936, ty=-1.9190702763541418, tz=0.3, qz=0.19866933079506124`；相对 +90° 的 `qz=0.70710678118654746, qw=0.70710678118654757`。检查容差 1e-9，共同外层变换后的误差小于此容差。

基础生产包 UUID `8a66876c-5175-5b26-a5e2-c36a26062e29` v1，10 个普通 Asset、两个普通 group、三条关系，声明原始字节见 [样例](../examples/instance-binding/README.md)。扩展模型亦完成生产导出 `16db25c7-2c86-50b5-9915-fc786493e906/v3`，几何、源模型、关系、report 全部校验通过；后续变更测试未覆盖已有导出版本。两份完整几何包留在本机 artifacts，候选安装包只携带小型声明样例，不携带用户模型几何。

生产端关系文件格式固定为 1.0，详见 [协议](INSTANCE_BINDING_PROTOCOL.md)。当前没有万象消费方代码、NAS 联调环境或正式验收签字；这些项目不以自动测试替代。
