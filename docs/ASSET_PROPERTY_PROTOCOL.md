# 资产属性协议（工程师版）

协议版本：`1.1.0`

本协议定义资产有哪些属性、每个属性值表示什么，以及不同资产之间如何建立连接关系。后续资产定义、资产使用和资产数据检查均以本协议为基础。

机器人名称 UUID、XML 引用与 NAS 目录解析遵循 [Robot Asset 引用协议](ROBOT_ASSET_REFERENCE_PROTOCOL.md)；普通 Asset 的身份算法和发布接口不变。

## 1. 基本属性

| 属性 | 可以填写的值 | 含义 | 示例 |
| --- | --- | --- | --- |
| `is_asset` | `1` / `0` | 是否把当前对象作为一个独立资产。 | `1` |
| `零件名` | 文本 | 资产的人类可读名称。 | `快换盘-下` |
| `class` | `movable` / `robot` / `station` / `structure` | 资产的主要分类。 | `movable` |
| `asset_version` | 从 `1` 开始的正整数 | 当前资产的内容版本。 | `1` |
| `can_hold_contents` | `1` / `0`，可省略 | 布尔值，表示硬件是否具备容纳能力。省略时按 `0` 处理。 | 锥形瓶填 `1` |
| `slot_type` | `place`、`mount`、`clamp`、`thread`、`press_fit`、`snap_fit` | 仅对 `slots_num > 0` 的资产填写。普通承托槽选 `place`。 | 提供夹持环安装位的瓶填 `clamp` |
| `engagement` | `loose`、`tight` | 资产的设计接合方式，默认选 `loose`。无槽资产也可定义接合方式。 | 固定局部相对位姿的设计填 `tight` |
| `设计原理` | 文本 | 资产采用什么机械、电气或控制原理实现功能。 | `气缸驱动平行夹持` |
| `设计目的` | 文本 | 资产要解决什么问题、实现什么用途。`class=robot` 时填写用于生成 UUID 的机器人名称。 | `夹持并转移压滤瓶`；Robot 填 `Hebe` |
| `升版说明` | 文本 | 当前版本相对上一版本修改了什么。 | `调整夹指行程` |

`class` 的含义：

| `class` | 含义 | 例子 |
| --- | --- | --- |
| `movable` | 能够被机器人移动、抓取或转移的资产。 | 工具、夹指、快换盘、试管、可搬运试管架 |
| `robot` | 提供运动执行能力的机器人。 | Hebe、Talos |
| `station` | 整体固定、机器人不会搬运，但机器人会与其发生放置、取出、定位、加工或换装交互的资产。 | 离心机、分析仪、固定工位、支架适配器、快换支架 |
| `structure` | 机器人不能移动且不需要与之交互的结构。 | 框架、护栏、纯支撑结构 |

`class` 与接口是两个独立维度。声明机械 `accepts_interfaces` 的资产须同时定义经核实的 `connection_interface`。固定工位的 `connection_interface` 可以用实际安装面、定位基准或紧固方式定义。是否另外建立显式承载关系仍取决于业务用途。

SOLIDWORKS 自定义属性的名称及复选框标签均为 `can_hold_contents`。选中写 `1`，取消选中写 `0`。资产 manifest 在顶层写入同名 JSON 布尔值。属性值为 `1` 时输出 `true`，为 `0` 或缺失时输出 `false`。瓶盖、底座等具有 CAD 模型的部件继续使用机械接口和承载关系。

SOLIDWORKS 文件级自定义属性是 `slot_type` 和 `engagement` 的唯一来源。资产有槽时，按实际机构选择 `slot_type`，例如由瓶提供安装位，瓶盖或夹持环作为子资产。只在槽数大于零时读取并输出 `slot_type`。资产可独立定义 `engagement`，例如无槽的盖与主体接合。满足这些条件的非空值写入资产 manifest 的 `properties`。消费端决定具体实例的接合状态，并独立处理外部底座、固定和停放约束。

| `slot_type` | 有效的 `engagement` | 定义 |
| --- | --- | --- |
| `place` | `loose` | 放置或承托，允许设计内的相对运动。 |
| `mount` | `loose`、`tight` | 通过安装面或连接基准定位的安装槽。 |
| `clamp` | `loose`、`tight` | 通过夹持机构和接触面约束的夹持槽。 |
| `thread` | `loose`、`tight` | 由配对螺纹旋合安装。 |
| `press_fit` | `loose`、`tight` | 由压入配合面安装，例如压入式盖或过盈配合。 |
| `snap_fit` | `loose`、`tight` | 由卡扣或弹性卡缘越过止位后扣合。 |

`tight` 表示设计会保持安装两侧的局部相对位姿，`loose` 表示设计允许局部相对运动。螺纹、压配或卡扣的形状证明安装方式，接合约束须另按设计核实。实际槽位、`slots_num` 和装配关系按其各自的设计依据维护。

## 2. 角色属性

`class` 表示资产主要是什么，下面的 `is_*` 属性表示资产还具有什么作用。它们可以组合使用。

| 属性 | 值 | 含义 | 例子 |
| --- | --- | --- | --- |
| `is_tool` | `1` / `0` | 是否是机器人末端工具。 | 夹指填 `1` |
| `is_fixture` | `1` / `0` | 是否能定位、夹持、承载或接收其他资产/工件。 | 夹具填 `1` |
| `is_quick_changer` | `1` / `0` | 是否是独立快换盘。 | 快换盘上下盘均填 `1` |
| `quick_changer_side` | `robot_side` / `tool_side` / 空 | 快换盘位于机器人侧还是工具侧。 | 上盘填 `robot_side` |
| `is_quick_changer_rack` | `1` / `0` | 是否是停放工具侧快换组件的快换支架。 | 快换支架填 `1` |
| `is_adjustable` | `1` / `0` | 安装位姿或空间布局位置是否允许调整。 | 可移动布置的支架填 `1` |

快换盘侧别定义：

```text
robot_side = 快换盘-上，安装在机器人侧
tool_side  = 快换盘-下，安装在工具侧，也可以停放在快换支架
```

末端夹指既是机器人使用的工具，又能夹持工件，因此可以同时定义：

```text
is_tool    = 1
is_fixture = 1
```

## 3. 接口属性

| 属性 | 含义 | 示例 |
| --- | --- | --- |
| `connection_interface` | 当前资产作为子级时，用什么接口连接父级。只能表示一个接口。 | `法兰-4xM6-PCD30` |
| `accepts_interfaces` | 当前资产作为父级时，可以接受哪些子级接口。多个接口用英文分号 `;` 分隔。 | `法兰-4xM6-PCD30;法兰-4xM8-PCD40` |

没有显式承载关系和机械 `accepts_interfaces` 时，普通资产（包括仅标记为 Fixture 的资产）和 Robot 可按用途留空接口。Tool 须填写 `connection_interface`，快换盘和快换架须填写两个接口字段。安装证据不足时记录具体缺项，保留已验证的机械接收接口。

建立显式承载关系时，父级的接受接口列表必须包含子级的连接接口名称，并且名称须完全一致。

```text
父级.accepts_interfaces
        ↓ 包含
子级.connection_interface
```

例如：

```text
机器人.accepts_interfaces = 法兰-4xM6-PCD30
工具.connection_interface = 法兰-4xM6-PCD30
```

该示例表示工具与机器人的机械兼容性。Robot 的接口可以作为元数据填写。显式承载关系的端点只支持普通 Asset 实例。

接口名称必须表达决定兼容性的机械特征：

- 法兰接口包含孔数、螺纹规格和分度圆，例如 `法兰-4xM6-PCD30`。
- 试管、瓶等容纳接口包含对象类型和关键几何尺寸，例如 `试管-D20`。
- 不使用单独的 `直径20`，因为它没有说明是哪一类对象。
- 不使用单独的 `试管-10ml`，因为容量相同的试管可能具有不同外径、高度或底部形状。
- 如果直径不足以决定兼容性，可以使用 `试管-D20-H100-圆底`。

当前协议中的接口示例：

| 接口名称 | 含义 |
| --- | --- |
| `法兰-4xM6-PCD30` | 4 个 M6 安装孔、分度圆直径 30 mm 的法兰兼容接口。 |
| `快换盘-A` | 快换盘-上和快换盘-下之间的配对接口。 |
| `工作台安装面` | 固定夹具、快换支架等连接工作台的接口。 |
| `压滤瓶夹持接口` | 夹具接收和夹持压滤瓶的工件接口。 |
| `试管-D20` | 以外径 20 mm 作为主要兼容条件的试管容纳接口。 |

接口名称表示机械兼容性。两个对象接口名称相同，表示机械接口兼容，不表示它们的角色相同。

上述机械接口名用于 SOLIDWORKS 内的绑定校验。资产导出清单使用 UUID 表示接口。`properties.connection_interface` 写入当前资产不带版本的 UUID。`properties.accepts_interfaces` 以分号分隔子资产 UUID，合并源属性已声明的 UUID 与当前装配中直接绑定的子资产 UUID。旧的机械接口名称不会被推断为某个子资产 UUID。

机械接口候选名称由 Wanxiang 在线接口库集中维护。现有模型保存的接口名称继续作为精确匹配键。参见 [接口候选库](INTERFACE_CATALOG_GUIDE.md)。

机械接口只表示可配合的实体部件。维护文件级属性时，从旧 `accepts_interface` 和 `accepts_interfaces` 中移除内容物标签，保留瓶盖、底座、工具等已核实的机械接口。`设计目的`、`设计原理` 和其它资产描述只写硬件用途、规格、容量和机械接口。容纳能力用布尔值 `can_hold_contents` 表达。

导出时，`properties.零件名` 同时作为发布显示名。插件按万象注册表、本地已保存清单和本次导出批次检查跨 UUID 重名，比较时忽略大小写和首尾空白。重名时用资产 UUID 加稳定后缀，同一资产的各版本可复用该名称。CAD 文件名保持原有值。

## 4. 槽位和二维码属性

| 属性 | 值 | 含义 | 示例 |
| --- | --- | --- | --- |
| `slots_num` | 大于或等于 `0` 的整数 | 由插件保存的总槽位数，包含空槽。 | 双工位快换架为 `2` |
| `has_QRcode` | `1` / `0` | 是否需要二维码标识。 | `1` |
| `QR_num` | 大于或等于 `0` 的整数 | 二维码数量。 | `2` |
| `QR_size` | 尺寸文本 | 单个二维码实体尺寸，单位由项目统一。 | `20mm` |
| `QR_spacing` | 大于或等于 `0` 的数值 | 多个二维码之间的间距，单位和测量基准由项目统一。 | `10` |

二维码示例：

```text
没有二维码：
has_QRcode = 0
QR_num     = 0
QR_size    =
QR_spacing = 0

有两个二维码：
has_QRcode = 1
QR_num     = 2
QR_size    = 20mm
QR_spacing = 10
```

## 5. assembly_package 版本属性

| 属性 | 值 | 含义 | 示例 |
| --- | --- | --- | --- |
| `assembly_version` | 从 `1` 开始的正整数 | SOLIDWORKS assembly_package（装配包）的版本。它不是 Asset 版本。 | `1` |

一个 assembly_package 可以包含多个不同 `asset_version` 的 Asset；`assembly_version` 不替代任何 Asset 自己的版本。本地与 Wanxiang 远端均使用 `assembly_package/` 顶层目录。

## 6. 典型资产定义

### 6.1 机器人

```text
is_asset               = 1
零件名                  = Hebe机器人
class                  = robot
is_tool                = 0
is_fixture             = 0
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 0
connection_interface   = 机器人底座.定位与紧固
accepts_interfaces     = 法兰-4xM6-PCD30
slots_num              = 0
is_adjustable          = 0
asset_version          = 1
设计目的                 = Hebe
```

Robot 的 XML 引用示例如下，UUID 由名称 `Hebe` 按 Robot Asset 引用协议生成；节点仍使用 `kind="robot"`：

```text
asset_id = 4ce608df-8e9e-5a8b-b084-894b8da37409:1
```

机器人资源由人工上传至 NAS 的 `robots/Hebe/v1/urdf/`，网格放在 `robots/Hebe/meshes/` 并由 URDF 引用。插件不导出机器人几何、URDF 或普通 Asset 包，不将机器人写入普通 `assets/` 注册表。目录保留原始名称，消费者扫描 `robots/` 的直接名称子目录建立 UUID 索引；UUID 不能反解为名称。

### 6.2 快换盘-上

```text
is_asset               = 1
零件名                  = 快换盘-上
class                  = movable
is_tool                = 0
is_fixture             = 0
is_quick_changer       = 1
quick_changer_side     = robot_side
is_quick_changer_rack  = 0
connection_interface   = 法兰-4xM6-PCD30
accepts_interfaces     = 快换盘-A
slots_num              = 0
is_adjustable          = 0
asset_version          = 1
```

含义：快换盘-上使用 `法兰-4xM6-PCD30` 连接机器人，并通过 `快换盘-A` 接收快换盘-下。

### 6.3 快换盘-下

```text
is_asset               = 1
零件名                  = 快换盘-下
class                  = movable
is_tool                = 0
is_fixture             = 0
is_quick_changer       = 1
quick_changer_side     = tool_side
is_quick_changer_rack  = 0
connection_interface   = 快换盘-A
accepts_interfaces     = 法兰-4xM6-PCD30
slots_num              = 0
is_adjustable          = 0
asset_version          = 1
```

含义：快换盘-下使用 `快换盘-A` 连接快换盘-上，并通过 `法兰-4xM6-PCD30` 安装工具。

### 6.4 末端夹指

```text
is_asset               = 1
零件名                  = 压滤瓶转移夹指
class                  = movable
is_tool                = 1
is_fixture             = 1
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 0
connection_interface   = 法兰-4xM6-PCD30
accepts_interfaces     = 压滤瓶夹持接口
slots_num              = 1
is_adjustable          = 0
asset_version          = 1
设计目的                 = 夹持并转移压滤瓶
```

含义：夹指使用 `法兰-4xM6-PCD30` 连接机器人或快换盘-下，并通过 `压滤瓶夹持接口` 接收工件。

### 6.5 快换支架

```text
is_asset               = 1
零件名                  = 快换支架
class                  = station
is_tool                = 0
is_fixture             = 1
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 1
connection_interface   = 快换支架.底板安装面
accepts_interfaces     = 快换盘-A
slots_num              = 2
is_adjustable          = 1
asset_version          = 1
设计目的                 = 停放未使用的工具侧快换组件
```

### 6.6 固定试管架

```text
is_asset               = 1
零件名                  = 50ml试管架治具
class                  = station
is_tool                = 0
is_fixture             = 1
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 0
connection_interface   = 试管架底座.工作台安装面
accepts_interfaces     = 试管-D20
slots_num              = 24
is_adjustable          = 1
asset_version          = 1
设计目的                 = 定位并承载50ml试管
```

固定试管架属于 `station`，接收机器人放入的外径 20 mm 试管，其向上接口取自已经核实的工作台安装面。

### 6.7 10 ml 试管

假设该试管实际外径为 20 mm，且直径足以决定与试管架的兼容性：

```text
is_asset               = 1
零件名                  = 10ml试管
class                  = movable
is_tool                = 0
is_fixture             = 0
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 0
connection_interface   = 试管-D20
accepts_interfaces     =
slots_num              = 0
is_adjustable          = 0
asset_version          = 1
设计目的                 = 提供10ml容量及机器人抓取定位面
```

这里 `10ml` 是资产规格，`试管-D20` 才是与试管架匹配的机械接口。

### 6.8 Station 中的试管层级

固定 Station 内建议只建模运行时会发生变化的两层连接：

```text
适配器底板（structure，或 is_asset=0）
└─ 支架适配器（station，connection_interface=支架适配器.底板定位面）
   └─ 试管支架（movable，connection_interface=试管支架-A）
      └─ 试管（movable，connection_interface=试管-D20）
```

支架适配器作为固定工位：

```text
class                  = station
is_fixture             = 1
connection_interface   = 支架适配器.底板定位面
accepts_interfaces     = 试管支架-A
slots_num              = 1
```

试管支架作为可搬运载具：

```text
class                  = movable
is_fixture             = 1
connection_interface   = 试管支架-A
accepts_interfaces     = 试管-D20
```

仅承担永久支撑的独立底板使用 `class=structure`，归入适配器几何的内部底板使用 `is_asset=0`。适配器的向上接口按实际安装基准定义。若底板作为独立资产接收可拆换的适配器，底板还需填写自己的向上接口和与适配器匹配的 `accepts_interfaces`，并按实际用途建立承载关系。

## 7. 资产连接关系

允许的连接：

```text
机器人 -> 工具
机器人 -> 快换盘-上
快换盘-上 -> 快换盘-下
快换盘-下 -> 工具
快换支架 -> 快换盘-下
```

组成的典型链路：

```text
机器人 -> 工具

机器人 -> 快换盘-上 -> 快换盘-下 -> 工具

快换支架 -> 快换盘-下 -> 工具
```

不允许的连接：

```text
机器人 -> 快换盘-下
快换盘-上 -> 工具
快换盘-下 -> 快换盘-上
快换支架 -> 快换盘-上
```

虽然快换盘-上和工具都可以使用 `connection_interface=法兰-4xM6-PCD30`，它们仍由角色属性区分：

```text
快换盘-上：is_quick_changer=1, quick_changer_side=robot_side
工具：     is_tool=1
```

因此每条有效连接必须同时满足：

```text
接口兼容
并且
父子角色允许
```

## 8. 版本示例

初始资产：

```text
asset_version = 1
升版说明       = 初始版本
```

夹指结构修改后的版本：

```text
asset_version = 2
升版说明       = 增大夹指开口行程并调整限位结构
```

Robot 的 UUID 仅由文件级 `设计目的` 中的名称生成，`asset_version` 单独表示引用版本。以下引用与第 6.1 节的 v1 使用相同 UUID：

```text
设计目的       = Hebe
asset_version = 2
asset_id      = 4ce608df-8e9e-5a8b-b084-894b8da37409:2
```

UUIDv5 namespace 为 `6ba7b811-9dad-11d1-80b4-00c04fd430c8`，名称输入为 `urn:solidworks-robot:v1:` 加规范化名称，不包含版本或长度编码。规范化固定为 Unicode `White_Space` 首尾裁剪、NFC、仅 ASCII `A-Z` 转小写；非 ASCII 大小写保留，精确字符集合见 [Robot Asset 引用协议](ROBOT_ASSET_REFERENCE_PROTOCOL.md)。名称规范化结果相同则 UUID 相同；名称改变且规范化结果不同时视为另一机器人身份。

NAS 新增、改名或删除机器人目录后，消费者必须刷新 UUID 索引；重复规范化名称、未知 UUID 或缺失 `vN` 版本应报错，不能选择其他目录或版本代替。可使用 [Python 参考实现](../scripts/robot_asset_reference.py) 的 `uuid NAME`、`index ROBOTS_ROOT`、`resolve ROBOTS_ROOT ASSET_ID` 命令复核。不保留旧机器人引用格式；包含 Robot 的已有装配包应提升 `assembly_version` 后重新导出。
