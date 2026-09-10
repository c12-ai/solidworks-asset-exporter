# 资产属性协议（工程师版）

协议版本：`1.1.0`

本协议定义资产有哪些属性、每个属性值表示什么，以及不同资产之间如何建立连接关系。后续资产定义、资产使用和资产数据检查均以本协议为基础。

## 1. 基本属性

| 属性 | 可以填写的值 | 含义 | 示例 |
| --- | --- | --- | --- |
| `is_asset` | `1` / `0` | 是否把当前对象作为一个独立资产。 | `1` |
| `零件名` | 文本 | 资产的人类可读名称。 | `快换盘-下` |
| `class` | `movable` / `robot` / `station` / `structure` | 资产的主要分类。 | `movable` |
| `asset_version` | 从 `1` 开始的正整数 | 当前资产的内容版本。 | `1` |
| `设计原理` | 文本 | 资产采用什么机械、电气或控制原理实现功能。 | `气缸驱动平行夹持` |
| `设计目的` | 文本 | 资产要解决什么问题、实现什么用途。 | `夹持并转移压滤瓶` |
| `升版说明` | 文本 | 当前版本相对上一版本修改了什么。 | `调整夹指行程` |

`class` 的含义：

| `class` | 含义 | 例子 |
| --- | --- | --- |
| `movable` | 能够被机器人移动、抓取或转移的资产。 | 工具、夹指、快换盘、试管、可搬运试管架 |
| `robot` | 提供运动执行能力的机器人。 | Hebe、Talos |
| `station` | 整体固定、机器人不会搬运，但机器人会与其发生放置、取出、定位、加工或换装交互的资产。 | 离心机、分析仪、固定工位、支架适配器、快换支架 |
| `structure` | 机器人不能移动且不需要与之交互的结构。 | 框架、护栏、纯支撑结构 |

`class` 与接口是两个独立维度。`station` 不会自动要求 `connection_interface`：Station 与机架、地面或底板永久固定且该关系不参与运行时装配校验时，该字段留空；只有 Station 本身是可拆换模块、需要对其上级连接进行建模时才填写。Station 仍可通过 `is_fixture=1` 和 `accepts_interfaces` 描述它接收的试管架、载具或快换组件。

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

父子接口关系：

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

表示该工具可以直接连接机器人。

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

## 4. 槽位和二维码属性

| 属性 | 值 | 含义 | 示例 |
| --- | --- | --- | --- |
| `slots_num` | 大于或等于 `0` 的整数 | 资产提供多少个安装槽、工位、停放位或容纳位置。 | 双工位快换架填 `2` |
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
connection_interface   =
accepts_interfaces     = 法兰-4xM6-PCD30
slots_num              = 0
is_adjustable          = 0
asset_version          = 1
设计目的                 = Hebe
```

Robot 的标识示例：

```text
robot_id = Hebe:1
```

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
connection_interface   =
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
connection_interface   =
accepts_interfaces     = 试管-D20
slots_num              = 24
is_adjustable          = 1
asset_version          = 1
设计目的                 = 定位并承载50ml试管
```

含义：固定试管架不能被机器人整体移动，但需要接收机器人放入的试管，因此属于 `station`；它接受外径 20 mm 的试管接口。其与工作台永久固定，所以 `connection_interface` 留空。

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
设计目的                 = 盛装并转移10ml样品
```

这里 `10ml` 是资产规格，`试管-D20` 才是与试管架匹配的机械接口。

### 6.8 Station 中的试管层级

固定 Station 内建议只建模运行时会发生变化的两层连接：

```text
适配器底板（structure，或 is_asset=0）
└─ 支架适配器（station，永久固定所以 connection_interface 为空）
   └─ 试管支架（movable，connection_interface=试管支架-A）
      └─ 试管（movable，connection_interface=试管-D20）
```

支架适配器作为固定工位：

```text
class                  = station
is_fixture             = 1
connection_interface   =
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

适配器底板若只承担永久支撑，应使用 `class=structure`；若只是支架适配器组件内部的普通零件，则使用 `is_asset=0`。只有适配器本身需要从底板拆换并校验连接时，才给适配器填写 `connection_interface`，同时在底板填写对应的 `accepts_interfaces`。

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

Robot 使用 `设计目的` 和 `asset_version` 表示引用版本：

```text
设计目的       = Hebe
asset_version = 2
robot_id      = Hebe:2
```
