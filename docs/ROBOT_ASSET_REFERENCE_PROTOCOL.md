# Robot 资产引用与 NAS 解析协议 v1

本协议公开机器人 UUID 的完整算法和下游名称反查方式。机器人使用统一的 `asset_id="UUID:版本"`，节点仍为 `kind="robot"`。普通 Asset 的 UUID、版本、目录和发布接口不受影响。

## 1. 目录与责任边界

机器人资源由人工上传至 NAS，目录结构保持如下形式。`robots_root` 指调用方已配置或传入的 `robots` 目录，不是新增的插件配置项。

```text
wanxiang_test/
  assets/                         # 普通资产：沿用原规则
  assembly_package/               # 装配包：沿用原规则
  robots/                         # robots_root
    Hebe/
      ...
    Talos/                        # 原始机器人目录名
      meshes/
        simplified/
          ...
      v1/
        urdf/
          ...
      v2/
        urdf/
          ...
```

SOLIDWORKS 导出器只生成机器人引用，不创建/上传 Robot Asset 包，不把机器人注册到普通 Asset 注册表，也不要求其出现在 `GET /asset/registry` 中。插件不会扫描 NAS；机器人资源存在性由下游解析器检查。

机器人目录名必须与 SOLIDWORKS **文件级“设计目的”**按第 3 节规则规范化后相同。`asset_version` 指定 `vN` 目录。文件名、组件实例名、“零件名”、配置、显示状态及文件创建时间不用于机器人 UUID。

## 2. XML 与版本

Talos v1 的 mesh 引用示例：

```xml
<!-- 位于 kind="robot" 的 node 内；节点 id、parent_id、pose 仍由原流程生成 -->
<mesh asset_id="be210481-a11b-5fb2-8002-bf9bd0b9afdb:1" />
```

下游必须先读取所属节点的 `kind`。`kind="robot"` 按本协议查找 `robots`；`kind="asset"` 按普通资产规则查找 `assets`。相同字段名不意味着两类资源使用同一目录。

机器人 `asset_id` 必须是小写、带连字符的 36 字符 RFC UUIDv5，接一个 ASCII 冒号 `:`，再接 `1..2147483647` 范围内的十进制正整数。不允许花括号、UUID 大写、前导零、空白或正负号。UUID 版本位必须为 `5`，variant 首个十六进制字符必须为 `8/9/a/b`。例如 `:01`、`:0`、`:1.0` 不合法。

UUID 标识机器人型号；版本只在冒号后。Talos v1、v2 共用同一 UUID，各自解析到 `Talos/v1/urdf` 和 `Talos/v2/urdf`。同型号的多个装配实例也共享该引用，通过各自的 `node.id` 和位姿区分。

新导出不再写 `robot_id`。新解析器不支持 `Talos:1` 等旧名称引用，不做旧新 ID 映射或兼容回退。

## 3. 公开的 UUID 算法

固定参数如下，所有实现必须逐字一致：

| 参数 | 值 |
|---|---|
| 算法 | RFC 9562 UUIDv5（SHA-1 名称哈希） |
| Namespace UUID | `6ba7b811-9dad-11d1-80b4-00c04fd430c8`（URL namespace） |
| 机器人专用前缀 | `urn:solidworks-robot:v1:` |
| UUID 的 name 输入 | 机器人专用前缀 + 规范化后的机器人名称 |
| 编码与输出 | 输入 UTF-8、无 BOM；输出小写 `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` |

### 3.1 名称规范化

按以下顺序处理：

1. 仅移除名称首尾属于下述固定集合的字符，内部空白保留。
2. 执行 Unicode NFC 规范化。
3. 拒绝空字符串、`.`、`..`、路径分隔符 `/` 和 `\`、Unicode 控制字符（类别 `Cc`）及非法 Unicode 序列。
4. **仅将 ASCII `A`–`Z` 转为 `a`–`z`**。其他字符保留，不能用全 Unicode `lower()` / `casefold()` 代替。

首尾空白集合为以下 Unicode 码点：

```text
U+0009..U+000D, U+0020, U+0085, U+00A0, U+1680,
U+2000..U+200A, U+2028, U+2029, U+202F, U+205F, U+3000
```

固定 ASCII 大小写规则避免不同语言、区域设置和 Unicode 大小写表导致 UUID 不一致。例如 `Talos`、`TALOS`、` talos ` 同身份；`É` 与 `é` 是不同身份，`e` + U+0301 与 `é` 经 NFC 后相同。U+200B、U+FEFF 不在首尾空白集合内，不能额外删除。

### 3.2 哈希输入与字节序

```text
normalized_name = normalize_robot_name(name)
uuid_name        = "urn:solidworks-robot:v1:" + normalized_name
robot_uuid       = UUIDv5(URL_NAMESPACE, uuid_name)
asset_id         = lower_D_format(robot_uuid) + ":" + decimal(asset_version)
```

对于 `Talos`，完整 name 输入是 **`urn:solidworks-robot:v1:talos`**。不追加版本、NAS 根目录、换行、NUL、长度前缀或分隔符。特别注意：这里不使用普通资产的 `Canonical.Join` 编码，也不复用普通资产的文件身份算法。

使用标准库 UUIDv5 时直接传上述 Namespace 和 name。自行实现时，将 **Namespace 的 16 个网络字节序字节**与 name 的 UTF-8 字节拼接，计算 SHA-1，取前 16 字节，设置版本和 variant 位：

```text
bytes[6] = (bytes[6] & 0x0f) | 0x50
bytes[8] = (bytes[8] & 0x3f) | 0x80
```

随后按 RFC UUID 网络字节序格式化。不要直接把 .NET `Guid.ToByteArray()` 的混合字节序作为网络字节序使用。本仓库的 `Uuid5.Create` 已处理此转换。

### 3.3 跨语言测试向量

| 原始名称 | 规范化名称 | UUID |
|---|---|---|
| `Talos` / ` TALOS ` | `talos` | `be210481-a11b-5fb2-8002-bf9bd0b9afdb` |
| `Hebe` | `hebe` | `4ce608df-8e9e-5a8b-b084-894b8da37409` |
| `e` + U+0301 / `é` | `é` | `ab40490a-e945-5e15-a365-38704430a7d8` |
| `É` | `É` | `834a7fbc-3c74-595a-aa63-bc1d5aac6d57` |
| `机器人` | `机器人` | `45d5373c-3bc3-5651-92b2-9c4eddfb990c` |

参考标准：[RFC 9562 §5.5](https://www.rfc-editor.org/rfc/rfc9562.html#section-5.5)。C# 实现位于 [IdentityAndProperties.cs](../src/SolidWorksAssetExporter.Core/IdentityAndProperties.cs) 的公开方法 `IdentityService.NormalizeRobotName`、`IdentityService.RobotUuid`；版本拼接使用现有 `IdentityService.AssetId`。

## 4. 下游 UUID → 名称 → URDF 解析

**UUIDv5 不能直接逆解出任意名称。** 下游通过 NAS 中已知名称建立反向索引，人工无需维护 UUID 对照表。

1. 枚举 `robots_root` 的**直接子目录**，每个目录表示一个机器人型号。忽略普通文件，不把 `v1`、`urdf` 或 `meshes` 等更深层目录当作型号。
2. 对每个原始目录名计算 `robot_uuid`，建立 `UUID → 原始目录路径` 索引。保留大小写和 Unicode 原始拼写；规范化名称只用于哈希，不得用它重建实际路径。
3. 拆分并校验 XML 的 `asset_id`，得到 UUID 和版本；查索引取得名称目录。
4. 拼接 `<原始名称目录>/v<十进制版本>/urdf`，检查目录存在，交给现有 URDF 加载器选择入口文件。不要猜测 `Talos.urdf` 等具体文件名。
5. URDF 的 meshes 引用继续由现有加载器按照原有 URI/相对路径规则处理。不搬动或改写资源；`robots/Talos/meshes` 与 `robots/Talos/v1/urdf` 的相对位置保持原样。

完整例子：

```text
扫描目录 robots/Talos
  → UUID be210481-a11b-5fb2-8002-bf9bd0b9afdb
  → 索引[UUID] = robots/Talos

XML kind="robot", asset_id="be210481-a11b-5fb2-8002-bf9bd0b9afdb:1"
  → 索引查找
  → robots/Talos/v1/urdf
  → 原有 URDF 入口与 meshes 加载流程
```

### 错误与缓存

| 情况 | 必须采取的行为 |
|---|---|
| `robots_root` 不存在或无法读取 | 报错，不能当作空资源库成功返回 |
| 多个目录映射到同一 UUID，例如 `Talos` 与 `talos` | 索引建立失败，列出冲突目录；不能任选一个 |
| UUID 未知 | 若使用缓存，刷新索引后重试一次；仍未知则报错，不回退到名称猜测或普通 Asset 注册表 |
| 版本目录或 `urdf` 缺失 | 报错，不自动选择最新版本 |
| 目录改名或索引过期 | 新增、移除、改名后刷新索引；规范化名称发生改变会产生新 UUID，引用需重新导出 |

可在启动时建立索引并缓存，在目录变更或人工上传完成后刷新。下面的参考解析器为了行为明确，每次解析均重新扫描。只有大小写或首尾空白变化且规范化结果不变的改名不会改变 UUID，但仍需刷新原始路径。

## 5. 可直接复用的 Python 参考实现

[scripts/robot_asset_reference.py](../scripts/robot_asset_reference.py) 使用 Python 3.8+ 标准库，无第三方依赖；可以复制到下游或作为算法对照。命令只读取目录，不创建、重命名、上传或修改 NAS 资源。

计算 UUID：

```bash
python3 scripts/robot_asset_reference.py uuid Talos
# be210481-a11b-5fb2-8002-bf9bd0b9afdb
```

生成包含原始名称与路径的 JSON 索引（`/mnt/nas/wanxiang_test/robots` 是示例挂载路径）：

```bash
python3 scripts/robot_asset_reference.py index /mnt/nas/wanxiang_test/robots
```

解析到 URDF 目录：

```bash
python3 scripts/robot_asset_reference.py resolve /mnt/nas/wanxiang_test/robots be210481-a11b-5fb2-8002-bf9bd0b9afdb:1
# /mnt/nas/wanxiang_test/robots/Talos/v1/urdf
```

代码集成可调用 `robot_uuid(name)`、`build_robot_index(robots_root)`、`parse_robot_asset_id(asset_id)` 和 `resolve_robot_asset(robots_root, asset_id)`。调用方负责先检查 XML `kind="robot"`；本模块不接管普通资产、XML 读取、URDF 入口选择或 meshes URI 解析。

## 6. 切换与验收

导出端和消费端同步切换，不保留旧字段或旧名称 ID 的兼容分支。NAS 名称目录无需转换为 UUID 目录，也无需建立新的入库 API。旧装配 XML 需要重新导出；已有本地含 Robot 包的内容指纹会改变，需要提升 `assembly_version`。普通资产规则及不含 Robot 装配包的指纹布局保持不变。

下游实现验收至少覆盖：

1. 本文全部 UUID 向量，以及相同名称在 v1/v2 下 UUID 相同。
2. XML 只按 `kind="robot"` 进入机器人解析，正确定位 `Talos/v1/urdf`，保留原始目录名。
3. 保持现有 URDF 与共享 meshes 的引用关系；在真实机器人资源上验证加载。
4. 重复名称、未知 UUID、缺失版本和旧 `Talos:1` 输入明确失败。
5. 普通资产解析及其 UUID、注册和上传流程保持原行为。

参考解析器测试：`python3 -m unittest discover -s tests -p 'test_robot_asset_reference.py'`。导出器回归测试沿用 `scripts/build-core.ps1`；真实 SOLIDWORKS 导出与下游实际 URDF 加载仍需在对应环境验收。
