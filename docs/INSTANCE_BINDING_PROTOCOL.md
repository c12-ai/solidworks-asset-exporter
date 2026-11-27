# 实例承载关系协议与操作说明

目标联调环境为 SOLIDWORKS 2025 SP5.0。含关系的装配包使用 1.3，空关系包使用 1.1。验证器兼容读取 1.0 至 1.4 装配包，1.4 仅供读取历史包。

## 组件选择与绑定

1. 打开已保存、可写的总装配，在 Asset / assembly_package 导出命令组中选择“承载关系”。面板先显示当前配置已保存的关系。
2. 在 SOLIDWORKS 图形区或原生设计树中选一个父资产，点击“读取父组件”。需要减少图形区干扰时，可点击“仅显示普通资产”，在当前视图临时隔离可见的普通资产。点击“恢复全部组件”或关闭面板，恢复隔离前的视图。
3. 在 SOLIDWORKS 图形区 Ctrl 多选或框选子组件，点击“添加所选子组件”。同一资产上的多个面会合并为一个实例；无法参与绑定的选择项会提示跳过。需要逐个选取时，勾选“连续点选子组件”。
4. 枪头等重复对象可用“同级同款批量添加”。以当前选择、待绑定列表中的选中项或最后添加项为起点，读取其直接 CAD 父级下同源同版本的可绑定实例。
5. 点击“应用绑定”核对并提交整批关系。每个子实例只绑定一个父级。确需同源资产分别由两个父级承载时，使用“原位复制并绑定”在当前配置插入一个独立 CAD 实例。若要将原实例切换到新父级，使用“替换全部父组件…”。已有关系的接口差异会标注在子组件明细的状态列。
6. 在关系区搜索、选择并高亮或解除指定父子关系。点击“校验已有关系”时，插件读取关系所涉及的实例和接口。启用“链式绑定”可将已绑定子组件作为下一轮父组件。
7. 点击“保存总装配”或按 Ctrl+S 写入关系，再重新分类预览、导出。切换文档或配置后，请重新打开承载关系面板。

只有普通 Asset 实例可参与绑定。选中 Asset 内部的几何时，选择归到包含它的外层 Asset 实例。

承载关系面板默认显示“资产关系”。按 Asset UUID 和版本汇总父资产、子资产及关联的实例关系数。切换到“实例绑定”可修改具体的承载实例。

点选普通资产时，图形区用临时彩色轮廓区分点中的实例、它的直接父实例和直接子实例、同资产的其他实例，以及资产级父、子资产的其余可见实例。资产级关系汇总当前配置内同资产实例的有效承载关系，跨越 CAD 分组。连续点选子组件时会暂停自动高亮。

## 维护接口与槽位

在承载关系面板顶部点击“接口属性 / 建议…”，可编辑本轮父子组件或所选资产的文件级接口和槽位。源文件相同的实例合并编辑。可以根据已绑定或本轮待绑定的直接子组件追加接受接口建议。保存前查询当前资产版本的发布状态，已发布时使用新的资产版本。

Agent 通过 `asset_update_interfaces` 维护资产文件级接口。启用 `sync_accepts_from_bindings` 时，插件追加当前配置已绑定子级的连接接口，保留已声明的兼容能力。`slots_num` 取自已确认的设计容量，包含空槽。当前父实例的绑定槽数和同源实例中观察到的最大绑定槽数分别统计。普通关系各占一个槽位，共享同一 `slot_id` 的关系合计一个槽位。应用绑定不会用这些统计推算设计容量。导出预检对同源同配置的一套满载代表核对容量，并逐实例报告超容量。

导出预检按同一源文件、精确资产版本和引用 CAD 配置合并容量声明提示。固定夹具（`class=station` 且 `is_fixture=1`）、声明可接受机械接口的资产或已有绑定子级的资产缺少 `slots_num` 时，返回 `slot_capacity_undeclared`。接受机械接口却声明 `slots_num=0` 时返回 `zero_capacity_with_mechanical_interfaces`。两种提示均不阻止导出。机械固定的工具属于工具侧快换总成时，该总成可保留机械接口并声明零个独立槽位。有实际绑定时仍检查超容量。硬件容纳能力用布尔值 `can_hold_contents` 表达。

插件保存前检查源文件身份、未保存修改、只读状态和链接表达式。每次属性写入前，源文件备份到 `%LOCALAPPDATA%\SolidWorksAssetExporter\PropertyBackups\<唯一目录>\`。保存失败时插件尝试恢复内存属性，并保留源文件打开供检查。工具参数和本机接口库见 [MCP 使用指南](MCP.md)。

## 存储与身份

总装配第三方流 `SWAssetExporter.Bindings.v1` 保存全部配置的关系。有共享槽位时使用存储格式 1.2，其余使用 1.1。读取端可打开此前带夹持状态的历史 1.3 流。配置使用 SW 的持久配置 ID 隔离。新建或复制配置时生成独立的配置 ID，改名保持同一 ID。配置 ID 的跨会话和改名语义见 [IConfiguration.GetID](https://help.solidworks.com/2022/english/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IConfiguration~GetID.html)。

端点使用总装配上下文中的 GetPersistReference3/GetObjectByPersistReference3，不以导出 node.id、名字、UUID、距离进行恢复。解析成功后还校验完整祖先组件 ID 上下文及源文件身份/路径，不能确认时要求解除重绑。源路径与创建信息仅为替换检测的附加保护，不用于搜索替代实例。依据：[持久引用说明](https://help.solidworks.com/2019/english/api/sldworksapiprogguide/Overview/Persistent_Reference_IDs.htm)。

只在 SaveToStorageNotify 中写入第三方流；成功写入且收到普通保存/另存为的 FileSavePostNotify 后才清除待保存状态。取消、写入失败和另存副本不确认原文档已保存。业务待保存状态优先于导出器内部解析 dirty 豁免。依据：[IGet3rdPartyStorage](https://help.solidworks.com/2021/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IModelDoc2~IGet3rdPartyStorage.html)。

同一路径、同一源模型的替换操作能否在所有 SW 编辑方式下保留旧持久引用仍需专项验收；不承诺任意替换操作天然维持身份。Pack and Go/源文件改名、另存到其他目录后，路径保护可能要求显式重绑。新增/复制配置的 ID 隔离已在实机验证；复杂 PDM 与多 SW 版本未声明支持。

## 导出文件

所有装配包均输出 `assembly-relations.json`，未绑定时 `relations=[]`。万象从 1.3 的 `placement` 字段解析并添加 `slots`。字段定义见 [JSON Schema](assembly-relations.schema.json)，旧版 1.0 真实 SW 导出样例见 [examples/instance-binding](../examples/instance-binding/README.md)。

| 字段 | 约定 |
| --- | --- |
| schema_version | 含关系为 `1.3`，空关系为 `1.1`。历史夹持关系格式 `1.4` 仅用于读取。 |
| assembly_uuid / assembly_version | 与同包 XML、export-report 一致 |
| length_unit / quaternion_order | 固定 `m` / `xyzw` |
| relations[].parent_node_id / child_node_id | 同包 XML 中唯一的普通 Asset 实例 ID。当前插件生成的装配包中每个 child 至多有一个 parent。 |
| relations[].pose | tx/ty/tz/qx/qy/qz/qw；子模型原点相对承载父模型原点 |
| relations[].placement | `slot_key` 是稳定槽位标识，`parent_asset_uuid` 和 `child_asset_uuid` 是不带版本的资产 UUID，`pose` 是该候选子资产相对父资产原点的最终安装位姿。 |

关系可按 `parent_node_id` 和 `placement.slot_key` 分组。共享槽位的多个候选具有相同的 `slot_key`，未共享槽位的关系各有独立的 `slot_key`。同组的 `placement.child_asset_uuid` 和 `placement.pose` 组成候选列表，`placement.pose` 已包含共享槽位基准和子级偏移。`parent_asset_uuid` 标识槽位所在的父资产。万象处理 1.3 时为分组赋予 `"001"` 等槽号。

`relations[].pose` 为 `inverse(T_world_parent) * T_world_child`。使用 SW 顶层装配上下文的 Transform2，该 API 已包含嵌套祖先变换。原 XML 的 CAD `parent_id` 和 `pose` 继续记录 CAD 树和位姿。[IComponent2.Transform2](https://help.solidworks.com/2019/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.IComponent2~Transform2.html)

### 槽位的硬件定义

业务父级是提供安装位的主体，子级是安装在该位置的可拆件。例如 1000 mL 圆形瓶是父资产，夹持环是子资产。资产文件仅在 `slots_num > 0` 时定义 `slot_type`，`engagement` 也适用于无槽资产。符合条件的文件级属性值随资产 manifest 的 `properties` 输出。装配关系记录槽位、工位、子资产和安装位姿。 [资产属性协议](ASSET_PROPERTY_PROTOCOL.md)

消费端控制实例的实际夹紧、松开与脱离。设计为 `tight` 的连接在夹紧时可保持两侧局部相对位姿，设计为 `loose` 的连接允许局部相对运动。局部联动独立于整组与底座、外壳、外部固定或停放位置的约束。

历史 1.4 装配包可含成对的 `relations[].placement.slot_type` 与 `relations[].engagement`。读取时按历史格式验证，包括共享槽类型一致且每槽至多一个 `tight`。这些历史字段属于旧实例数据。

### 将新子组件绑定到已有槽位

在 CAD 中把新子组件定位到它装在目标槽位时的位置。按正常流程选父组件并添加待绑定子组件，然后在“实例绑定”选项卡选中目标槽位的一条已绑定子关系，点击“复用所选槽位…”并保存总装配。首次共享时，参考子组件须仍处于定义槽位的位置。插件保存它此刻相对父级的位姿作为固定槽位，参考子级的偏移为单位位姿。后续沿用该槽位，按新子级的当前 CAD 位姿计算并保存平移和旋转偏移。若位置和方向重合，偏移就是零平移和单位旋转。接口仍按父子属性校验。

从 1.2 起，每个共享槽位在文件顶层的 `slots` 记录一次，包含 `slot_id`、`parent_node_id` 和父级坐标系中的基准位姿。关联的每条关系记录相同的 `slot_id` 和独立的 `slot_offset`。从 1.3 起，`placement.pose` 写入 `槽位位姿 × 子级偏移`。同一槽位的多个子组件是备选项。独立槽位的 `placement.pose` 等于 `relations[].pose`。解除参考子组件的绑定后，只要还有子组件引用，槽位 ID 和位姿就继续保留。

关系按父 ID、子 ID 排序，禁止自绑定、环和重复父子对。插件可读取历史多父关系，发布前须修正到每个子实例至多一个父级。每条边独立计算相对父组件的位姿。接口名称按属性协议精确匹配，接受列表用英文分号分隔。机器人端快换盘只接受工具端快换盘，快换架接受工具端快换盘或可直接承载的 Tool，工具端快换盘接收 Tool。机器人端快换盘不作为本功能的被承载子对象。编辑器、预览和导出共用接口校验。

快换架直接承载移液器时，支架的 `accepts_interfaces` 须包含移液器的 `connection_interface`，并使用 `is_quick_changer_rack=1` 和 `class=station`。同一个装配需要同时表达工具侧持有与支架停放时，按业务需求分别使用两个同源实例。若支架承载的是工具侧快换盘，可使用“支架→工具侧快换盘→移液器”链。

子组件为 Tool 时，`工具安装.移液器` 与 `移液器` 在接口校验中等价。

隐藏、抑制、包络、删除、替换、失去独立 Asset 身份或变为接口不兼容的已绑定实例不会被静默移除，阻止装配包导出，用户可恢复对象或显式解除。未绑定对象继续正常导出。

`export-report` 的 `relations_schema_version` 必须与关系文件一致。文件清单包含关系文件大小和 SHA-256。复用及上传前校验关系文件、同包身份、端点、图结构和共享槽位引用。ZIP 包含原样关系文件。

格式标识、排序后的关系和位姿进入装配包指纹。首次迁移、绑定变更或位姿变化导致指纹变化时，插件为已有包分配新的 `assembly_version`。预览后修改关系需要保存并重新预览。

## 安装与本机升级

首次安装：关闭 SOLIDWORKS，完整解压发布 ZIP，以管理员身份运行 `Install.cmd`。

已存在系统 Add-in 注册与每用户 COM 注册时，可以关闭 SOLIDWORKS 后运行 `Install-UserUpdate.ps1`。该脚本将 DLL 放到 `%LOCALAPPDATA%\SolidWorksAssetExporter\Addin`，备份原 CodeBase，并更新当前用户的 COM 路径；它不会改动系统 DLL。它是现有安装的升级方式，不能替代首次系统注册。

每用户升级回退：关闭 SOLIDWORKS 后，用 `Restore-UserUpdate.ps1 -BackupFile <安装输出的 com-codebases.json>` 恢复原注册路径和已有用户载荷。需卸载时，先回退每用户升级，再使用原系统卸载脚本。

完整 P0 验收进度见 [验收记录](INSTANCE_BINDING_TEST_RESULTS.md)。候选包不表示所有交互、破坏性变更和 P1 工作流均已验收。
