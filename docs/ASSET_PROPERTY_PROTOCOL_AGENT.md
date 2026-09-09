# Asset Property Protocol（Agent 版）

协议标识：`solidworks-asset-property-protocol`

协议版本：`1.0.0`

本文是 Asset 属性的规范定义。资产识别、资产连接、资产使用、数据校验和后续扩展均以本协议为基础。

## 1. 数据模型

每个 Asset 由一组键值属性描述。属性在存储层均为字符串，Agent 必须按本协议声明的逻辑类型解释。

规范布尔值：

```text
1 = true
0 = false
```

兼容读取值：

```text
true / false
yes / no
```

Agent 生成新数据时必须使用 `1` 或 `0`。

列表字段使用英文分号 `;` 分隔：

```text
接口-A;接口-B
```

列表项去除首尾空格，不得包含空项或重复项。

## 2. 属性定义

| 属性 | 逻辑类型 | 值域 | 含义 | 示例 |
| --- | --- | --- | --- | --- |
| `is_asset` | boolean | `1` / `0` | 当前对象是否作为独立 Asset。`1` 表示它具有独立身份、属性和版本。 | `1` |
| `零件名` | string | 非空文本 | Asset 的人类可读名称。名称描述对象本身，不承担身份或版本功能。 | `快换盘-上` |
| `class` | enum | `movable` / `robot` / `equipment` / `structure` | Asset 的互斥主分类，表示“它主要是什么”。 | `movable` |
| `is_tool` | boolean | `1` / `0` | Asset 是否作为机器人末端工具使用。 | `1` |
| `is_fixture` | boolean | `1` / `0` | Asset 是否具有定位、夹持、承载或接收其他 Asset/工件的能力。 | `1` |
| `is_quick_changer` | boolean | `1` / `0` | Asset 是否为独立快换盘。快换盘不使用单独的 `class`。 | `1` |
| `quick_changer_side` | enum | `robot_side` / `tool_side` / 空 | 快换盘所属侧别。`robot_side` 表示机器人侧上盘；`tool_side` 表示工具侧下盘。 | `robot_side` |
| `is_quick_changer_rack` | boolean | `1` / `0` | Asset 是否为停放工具侧快换组件的快换支架。 | `1` |
| `connection_interface` | interface-id | 单个非空接口名或空 | 当前 Asset 作为子级时，用来连接父级的接口。 | `法兰-4xM6-PCD30` |
| `accepts_interfaces` | interface-id list | 以 `;` 分隔的接口名或空 | 当前 Asset 作为父级时，能够接收的子级接口集合。 | `法兰-4xM6-PCD30;法兰-4xM8-PCD40` |
| `slots_num` | integer | 大于或等于 `0` | Asset 提供的安装槽、工位、停放位或容纳位置数量。`0` 表示不提供槽位。 | `2` |
| `is_adjustable` | boolean | `1` / `0` | Asset 的安装位姿或空间布局位置是否允许调整。 | `1` |
| `asset_version` | integer | 大于 `0` | Asset 内容版本，从 `1` 开始递增。 | `1` |
| `has_QRcode` | boolean | `1` / `0` | Asset 是否具有二维码标识需求。 | `1` |
| `QR_num` | integer | 大于或等于 `0` | 二维码数量。 | `2` |
| `QR_size` | string | 项目约定的尺寸表达 | 单个二维码的实体尺寸。协议不规定单位，单位由项目统一。 | `20mm` |
| `QR_spacing` | number | 大于或等于 `0` | 多个二维码之间的间距。单位和测量基准由项目统一。 | `10` |
| `设计原理` | string | 文本或空 | Asset 实现功能所采用的机械、电气或控制原理。 | `气缸驱动平行夹持` |
| `设计目的` | string | 非空文本 | Asset 要解决的问题、目标和预期用途。Robot 使用该值作为 `robot_id` 名称段。 | `Hebe` |
| `升版说明` | string | 文本或空 | 当前版本相对上一版本的修改内容和原因。 | `调整夹指行程` |
| `assembly_version` | integer | 大于 `0` | Project 根装配版本，不是 Asset 内容版本。 | `1` |

## 3. `class` 语义

| 值 | 语义 | 典型对象 |
| --- | --- | --- |
| `movable` | 能够被机器人移动、抓取或转移的对象。 | 工具、夹指、快换盘、试管、可搬运样品载具 |
| `robot` | 提供运动执行能力的机器人。 | Hebe、Talos |
| `equipment` | 机器人不能移动其整体，但需要与之交互的对象。 | 离心机、分析仪、固定试管架、快换支架 |
| `structure` | 机器人不能移动且不需要与之交互的结构。 | 框架、护栏、纯支撑结构 |

`class` 是单值主分类；`is_tool`、`is_fixture`、`is_quick_changer` 等是可组合角色。禁止使用旧值 `moveable`。

## 4. 角色语义

### 4.1 Robot

判定：

```text
class = robot
```

约束：

```text
is_tool               = 0
is_fixture            = 0
is_quick_changer      = 0
is_quick_changer_rack = 0
```

Robot 的 `accepts_interfaces` 表示机器人末端可接受的安装接口。

### 4.2 Tool

判定：

```text
is_tool = 1
```

Tool 的 `connection_interface` 表示自身连接机器人或工具侧快换盘的安装接口。Tool 如果还能够夹持、定位或接收工件，可以同时设置 `is_fixture=1`。

### 4.3 Fixture

判定：

```text
is_fixture = 1
```

Fixture 的 `accepts_interfaces` 表示它能够接收的工件接口或子级 Asset 接口。Fixture 可以是 `movable`，也可以是 `structure`。

### 4.4 QuickChangerRobotSide

判定：

```text
is_quick_changer  = 1
quick_changer_side = robot_side
```

语义：安装在机器人侧的快换盘-上。它通过 `connection_interface` 连接 Robot，通过 `accepts_interfaces` 接收工具侧快换盘。

### 4.5 QuickChangerToolSide

判定：

```text
is_quick_changer  = 1
quick_changer_side = tool_side
```

语义：安装在工具侧的快换盘-下。它通过 `connection_interface` 连接机器人侧快换盘，通过 `accepts_interfaces` 接收 Tool。

### 4.6 QuickChangerRack

判定：

```text
class                 = equipment
is_quick_changer_rack = 1
```

语义：用于停放未使用的工具侧快换组件。它的 `accepts_interfaces` 表示可停放的快换配对接口。

## 5. 接口协议

对每条有方向的安装关系：

```text
父级.accepts_interfaces CONTAINS 子级.connection_interface
```

两个字段的含义不得互换：

```text
connection_interface = 当前 Asset 作为子级向上连接时提供的接口
accepts_interfaces   = 当前 Asset 作为父级向下接收时允许的接口
```

接口名称描述真实机械兼容性；角色顺序由第 6 节角色矩阵约束。接口名称相同不代表角色相同。

接口 ID 必须表示决定兼容性的机械特征：

- 螺栓法兰包含孔数、螺纹规格和分度圆，例如 `法兰-4xM6-PCD30`。
- 容纳类接口包含对象类型和决定兼容性的几何特征，例如 `试管-D20`。
- `直径20` 缺少对象域，不得作为正式接口 ID。
- `试管-10ml` 只表达容量；除非容量已经对应唯一、稳定的机械标准，否则不得作为接口 ID。
- 当直径不足以决定兼容性时继续增加必要几何特征，例如 `试管-D20-H100-圆底`。

当前协议示例接口：

| 接口 ID | 含义 |
| --- | --- |
| `法兰-4xM6-PCD30` | 4 个 M6 安装孔、分度圆直径 30 mm 的法兰兼容接口。用于机器人末端、快换盘和工具安装面。 |
| `快换盘-A` | 快换盘-上与快换盘-下之间的配对接口族。 |
| `工作台安装面` | 固定结构连接工作台的项目约定接口。 |
| `压滤瓶夹持接口` | 夹具接收压滤瓶的工件接口。 |
| `试管-D20` | 以外径 20 mm 作为主要兼容条件的试管容纳接口。 |

## 6. 父子角色协议

连接只有在“接口匹配”和“角色匹配”同时成立时才有效。

| 父级角色 | 允许的子级角色 | 典型含义 |
| --- | --- | --- |
| Robot | Tool | 机器人直接安装工具。 |
| Robot | QuickChangerRobotSide | 机器人安装快换盘-上。 |
| QuickChangerRobotSide | QuickChangerToolSide | 快换盘上下盘配对。 |
| QuickChangerToolSide | Tool | 快换盘-下安装工具。 |
| QuickChangerRack | QuickChangerToolSide | 快换支架停放工具侧快换组件。 |

以下角色顺序无效，即使接口名称相同：

```text
Robot -> QuickChangerToolSide
QuickChangerRobotSide -> Tool
QuickChangerToolSide -> QuickChangerRobotSide
QuickChangerRack -> QuickChangerRobotSide
```

有效链路示例：

```text
Robot -> Tool
Robot -> QuickChangerRobotSide -> QuickChangerToolSide -> Tool
QuickChangerRack -> QuickChangerToolSide
QuickChangerRack -> QuickChangerToolSide -> Tool
```

## 7. 组合约束

```text
is_quick_changer=1
    => quick_changer_side in {robot_side, tool_side}
    => connection_interface 非空
    => accepts_interfaces 非空

is_quick_changer=0
    => quick_changer_side 为空

is_quick_changer_rack=1
    => class=equipment
    => accepts_interfaces 非空

is_tool=1
    => connection_interface 非空

is_fixture=1
    => accepts_interfaces 非空

class=robot
    => accepts_interfaces 非空
    => is_tool=0
    => is_fixture=0
    => is_quick_changer=0
    => is_quick_changer_rack=0
```

二维码组合：

```text
has_QRcode=0 => QR_num=0, QR_spacing=0, QR_size 为空
has_QRcode=1 => QR_num>0, QR_size 非空
QR_num>1     => QR_spacing 按项目约定表达
```

## 8. 完整示例

### Robot

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

由 `设计目的` 和 `asset_version` 组成 Robot 引用：

```text
robot_id = Hebe:1
```

### 快换盘-上

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
设计目的                 = 安装在机器人末端并接收工具侧快换盘
```

### 快换盘-下

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
设计目的                 = 与机器人侧快换盘配对并安装末端工具
```

### 末端夹指 Tool

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
设计目的                 = 机器人末端夹持并转移压滤瓶
```

### 快换支架

```text
is_asset               = 1
零件名                  = 快换支架
class                  = equipment
is_tool                = 0
is_fixture             = 1
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 1
connection_interface   = 工作台安装面
accepts_interfaces     = 快换盘-A
slots_num              = 2
is_adjustable          = 1
asset_version          = 1
设计目的                 = 停放未使用的工具侧快换组件
```

### 固定试管架

```text
is_asset               = 1
零件名                  = 50ml试管架治具
class                  = equipment
is_tool                = 0
is_fixture             = 1
is_quick_changer       = 0
quick_changer_side     =
is_quick_changer_rack  = 0
connection_interface   = 工作台安装面
accepts_interfaces     = 试管-D20
slots_num              = 24
is_adjustable          = 1
asset_version          = 1
设计目的                 = 定位并承载50ml试管
```

### 10 ml 试管

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

## 9. 版本与身份语义

- `asset_version` 表示同一 Asset 身份下的内容版本。
- 首个版本为 `1`，后续内容变更依次增加。
- `asset_version` 不用于生成 Asset UUID。
- Robot 的引用格式固定为 `设计目的:asset_version`，例如 `Hebe:1`。
- `assembly_version` 只描述 Project 根装配版本，不替代任何 Asset 的 `asset_version`。
