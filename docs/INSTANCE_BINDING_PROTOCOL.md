# 实例承载关系协议与操作说明

实现版本：1.1.0-rc4（文件版本 1.1.0.3）；关系格式：1.1；目标联调环境：SOLIDWORKS 2025 SP5.0。可读取 rc1/rc2/rc3 的 1.0 关系；新增或修改关系后使用支持多父的 1.1 格式，旧插件不能编辑新存储。

## 操作

1. 打开已保存、可写的总装配，在 Asset / assembly_package 导出命令组中选择“承载关系”。
2. 在装配树或图形区选一个组件，点击“父组件 · 读取单选”。面板显示实例名称、资产版本和完整层级路径，自动清理 SW 当前选择并开启连续点选。
3. 逐个点击子组件，清单自动累计，不需要按住 Ctrl 或每次点击读取。也可关闭“连续点选子组件”，在 SW 多选后点击“添加当前选择”。重复实例自动去重，可移除误选项或“清空本轮”。选择事件立即缓存，界面稍后统一更新，避免快速连续点击漏项。
4. 枪头等大量重复对象：先添加一个子组件，再点击“同级同款批量添加”。有选中清单项时使用这些项，否则使用最后添加的子项；仅添加同一 CAD 父级下、同源文件身份及资产版本的独立普通 Asset，结果先进入待绑定清单。不会跨重复子装配自动分配归属。
5. 点击“应用绑定”，在可滚动的确认清单中核对本轮父子和所有已有父组件。允许同一个子实例拥有多个父实例；普通应用新增关系并保留已有关系，相同父子对重复应用不产生重复记录。整批校验成功才更新关系。成功后自动清空本轮及 SW 选择；取消或校验失败保留待绑定清单。
6. 关系区左侧每个父实例仅一行，显示子组件数量；右侧子明细增加“父级数”，悬停可查看全部父组件。共享子实例会分别出现在各个父组件组中。解除所选关系或整组只移除当前父组件的边，保留这些子组件与其他父组件的关系。可点击“将所选子项加入本轮”，读取新父后用“应用绑定”新增关系；只有明确要移除全部旧父时才使用“替换全部父组件…”，确认窗口列出全部旧父，替换后仅保留本轮父。搜索支持父/子名称、路径和 Asset ID。
7. 开启“链式绑定”后，单个子组件在应用成功后自动成为下一轮父组件；多个子组件需要从“下一父组件”下拉框选一个继续。也可以在已绑定明细中选一个子项，点击“将所选子项作为父”。已有上游关系保留，下一层继续遵守接口、角色、父子对去重和无环规则。
8. 点击面板“保存总装配”或回到 SW 按 Ctrl+S，再重新分类预览、导出。保存按钮走 SW 原生保存事件；成功后状态变为已保存，失败/取消保留待保存记录。关闭总装配而不保存会丢弃本次修改。

“查看全部 / 刷新”清除搜索并重新验证关系。未绑定不表示已确认空槽位。切换文档/配置后需重新打开面板。高亮关系会暂停连续点选，避免把高亮对象自动加入待绑定清单。

仅允许当前导出计划中的普通 Asset 实例。不支持总装配根、group、workstation、meshes、Robot 以及 Asset 硬边界内部零件。绑定不修改配合、位置、CAD 层级和 slots_num。

## 接口属性编辑与建议

在“承载关系”面板点击“接口属性 / 建议…”。优先编辑本轮已读取的父/子组件；本轮为空时使用所选已有父组件组；没有关系组时使用 SW 当前选中的普通 Asset。源文件列表按文件合并，200 个同源枪头只显示一项，选择后可以修改 `connection_interface` 和 `accepts_interfaces`。

1. 子资产的 `connection_interface` 使用单个接口名；可从下拉框选择或输入新名称。选项来自当前装配和本机接口库，选项本身不代表机械兼容性。
2. 选父资产，点击“根据子组件生成建议”，从明确选择或绑定的直接子组件读取已保存的 `connection_interface`，去重后追加到接受接口勾选项，保留原有能力。缺失子接口时提示先补齐；不读取其他资产尚未确认的编辑草稿。
3. 核对前后值、源文件路径和资产版本，点击“检查并保存所选资产…”，再次确认后才写入并保存这个源文件。取消保存不会写入，也不会升版。只读、已有未保存修改、链接表达式、编辑期间属性变化或组件身份失效会阻止写入。
4. 接口属于资产内容。默认 `asset_version` 加一；仅在用户明确勾选“确认此资产尚未发布”时允许保留原版本，不能降版。该勾选是用户声明，不是服务端发布状态查询，导出阶段仍执行版本校验。
5. 保存后回到绑定面板核对关系，再保存总装配并重新分类预览。同源文件的所有实例、所有装配引用都会受属性修改影响；界面中的“所选 N 实例”不是影响范围上限。

`accepts_interfaces` 仍是导出协议中的文件级自定义属性，不再要求手工进入 SW 属性页填写。建议生成只减少录入，不把“当前装着什么”直接当作完整兼容性定义；移除绑定不会缩减已有接受接口。绑定不自动生成接口、不改 `slots_num`，也不声明空槽位或容量已满足。

本机接口库位于 `%APPDATA%\SolidWorksAssetExporter\interfaces.json`。只有成功保存的接口名称才追加到该库；它是当前 Windows 用户的名称列表，不是团队共享服务。取消窗口不写库；库损坏时保留原文件并提示，资产保存成功而库更新失败会分别报告。

每次属性写入前，源文件备份到 `%LOCALAPPDATA%\SolidWorksAssetExporter\PropertyBackups\<唯一目录>\`。保存失败会尝试恢复内存属性，并保留源文件打开供检查；备份位置显示在错误信息中，不承诺磁盘事务回滚。不要在未检查的情况下关闭失败的源文件。

接口编辑验收范围见 [接口交互验收记录](INSTANCE_BINDING_INTERFACE_RESULTS.md)。

## 存储与身份

总装配第三方流 `SWAssetExporter.Bindings.v1` 保存全部配置的关系。配置使用 SW 的持久配置 ID 隔离；新建/复制配置不继承，改名保持同一 ID。配置 ID 的跨会话和改名语义见 [IConfiguration.GetID](https://help.solidworks.com/2022/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IConfiguration~GetID.html)。

端点使用总装配上下文中的 GetPersistReference3/GetObjectByPersistReference3，不以导出 node.id、名字、UUID、距离进行恢复。解析成功后还校验完整祖先组件 ID 上下文及源文件身份/路径，不能确认时要求解除重绑。源路径与创建信息仅为替换检测的附加保护，不用于搜索替代实例。依据：[持久引用说明](https://help.solidworks.com/2019/english/api/sldworksapiprogguide/Overview/Persistent_Reference_IDs.htm)。

只在 SaveToStorageNotify 中写入第三方流；成功写入且收到普通保存/另存为的 FileSavePostNotify 后才清除待保存状态。取消、写入失败和另存副本不确认原文档已保存。业务待保存状态优先于导出器内部解析 dirty 豁免。依据：[IGet3rdPartyStorage](https://help.solidworks.com/2021/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDoc2~IGet3rdPartyStorage.html)。

同一路径、同一源模型的替换操作能否在所有 SW 编辑方式下保留旧持久引用仍需专项验收；不承诺任意替换操作天然维持身份。Pack and Go/源文件改名、另存到其他目录后，路径保护可能要求显式重绑。新增/复制配置的 ID 隔离已在实机验证；复杂 PDM 与多 SW 版本未声明支持。

## 导出文件

新增 `assembly-relations.json`，所有新装配包均输出，未绑定时 `relations=[]`。字段定义见 [JSON Schema](assembly-relations.schema.json)，旧版 1.0 真实 SW 导出样例见 [examples/instance-binding](../examples/instance-binding/README.md)。当前生产端输出 1.1 契约以明确多父语义；万象消费方需支持该格式及多父图，本仓库不包含消费方代码，远端导入联调需独立完成。

| 字段 | 约定 |
| --- | --- |
| schema_version | 新导出固定 `1.1`；验证器兼容旧 `1.0` 单父关系文件 |
| assembly_uuid / assembly_version | 与同包 XML、export-report 一致 |
| length_unit / quaternion_order | 固定 `m` / `xyzw` |
| relations[].parent_node_id / child_node_id | 同包 XML 中唯一的普通 Asset 实例 ID；一个 child 可关联多个不同 parent，每个父子对最多一条 |
| relations[].pose | tx/ty/tz/qx/qy/qz/qw；子模型原点相对承载父模型原点 |

位姿为 `inverse(T_world_parent) * T_world_child`。使用 SW 顶层装配上下文的 Transform2；该 API 已包含嵌套祖先变换，不能再次连乘祖先。依据：[IComponent2.Transform2](https://help.solidworks.com/2019/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IComponent2~Transform2.html)。原 XML 的 CAD parent_id/pose 不变。接口偏移和空槽位不在本协议中。

关系按父 ID、子 ID 排序；允许多父形成有向无环图，禁止自身、环、重复父子对。每条边独立计算相对父组件的位姿，共享子实例只在 CAD XML 中保留一个节点。接口名称按属性协议精确匹配，接受列表用英文分号分隔。连接双方还校验现有角色属性：机器人端快换盘只接受工具端快换盘；快换架接受工具端快换盘，也可直接承载 Tool（例如移液器）；工具端快换盘接收 Tool；机器人端快换盘不作为本功能的被承载子对象。编辑器、预览和导出共用此校验。

快换架直接承载移液器时，支架的 `accepts_interfaces` 必须包含移液器的 `connection_interface`，无需取消支架的 `is_quick_changer_rack=1`。支架仍须为 `class=station`。若移液器实例已经由工具侧快换盘承载，可以“应用绑定”再添加支架作为父组件，保留两个父级；若只想保留支架，则用“替换全部父组件…”。原有“支架→工具侧快换盘→移液器”链仍然有效；已有直接承载关系可重新校验，无需重新绑定。

隐藏、抑制、包络、删除、替换、失去独立 Asset 身份或变为接口不兼容的已绑定实例不会被静默移除，阻止装配包导出，用户可恢复对象或显式解除。未绑定对象继续正常导出。

export-report 输出 `relations_schema_version=1.1`，必须与关系文件的格式版本一致；文件清单包含关系文件大小/SHA-256。复用及上传前校验关系文件、同包身份、端点和图结构。验证器仍支持版本一致的旧 1.0 单父包；多父包不得标成 1.0。ZIP 沿用原流程，包含原样关系文件，不新增远端完成标记或原子发布承诺。

格式标识及排序后的关系/位姿进入装配包指纹。首次迁移、绑定/解绑/更换、位姿变化都要求已有包提升 assembly_version。预览后任何关系修改均要求保存并重新预览。旧包缺失文件只表示没有显式关系信息，不推断归属或装配完整性。

## 安装与本机升级

首次安装：关闭 SOLIDWORKS，完整解压发布 ZIP，以管理员身份运行 `Install.cmd`。

已存在系统 Add-in 注册与每用户 COM 注册时，可以关闭 SOLIDWORKS 后运行 `Install-UserUpdate.ps1`。该脚本将 DLL 放到 `%LOCALAPPDATA%\SolidWorksAssetExporter\Addin`，备份原 CodeBase，并更新当前用户的 COM 路径；它不会改动系统 DLL。它是现有安装的升级方式，不能替代首次系统注册。

每用户升级回退：关闭 SOLIDWORKS 后，用 `Restore-UserUpdate.ps1 -BackupFile <安装输出的 com-codebases.json>` 恢复原注册路径和已有用户载荷。需卸载时，先回退每用户升级，再使用原系统卸载脚本。

完整 P0 验收进度见 [验收记录](INSTANCE_BINDING_TEST_RESULTS.md)。候选包不表示所有交互、破坏性变更和 P1 工作流均已验收。
