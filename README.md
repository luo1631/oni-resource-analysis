# 缺氧资源分析

缺氧资源分析是《缺氧》的独立玩家模组，用于按星球查看理论产耗、游戏内采集的实际收支及近 10 周期趋势。`v1.0.1` 面向 U60（764025 起）。[下载安装包](https://github.com/luo1631/oni-resource-analysis/releases/latest)

进入存档后，点「资源」左侧的「收支」。默认跟踪水、电力、氧气。勾选资源后可应用或直达其他页并保存。流向图默认隐藏氧气、电力，可用顶栏开关显示。

## 计算口径

| 对象 | 读取与折算 |
| --- | --- |
| 食物、复制人 | 食物用 kcal。口粮和呼吸理论取个体修正。实际吸入质量按气体与所在星球计，含服装及仿生氧气罐。 |
| 卫生设施 | 按膀胱填满频率、单次用水用土和排放估算。同类厕所共享需求，混用时分列备选。 |
| 栽培植物 | 读取灌溉、生长与原生产量倍率。关自动采收时将已知的成熟后自然掉果等待计入收获间隔。温室同种植物保留「农夫之触」多数状态估计。 |
| 驯化动物 | 仅畜舍。未来 10 周期按成熟、繁殖进度、寿命和同舍条件分段。幼体成熟后和供料待确认的产蛋列作备选。实测按原生排蛋事件统计蛋种。 |
| 建筑 | 转换按流量。加工含在制进度、排单、当前工人和适用的照明、实验室与支援加成。蒸汽涡轮按入口工况和最小结算批次估算功率与热流。 |
| 间歇泉 | 已分析的喷口显示活跃期均产、无阻塞长期均值、当前阶段及计时。单泉休眠缓冲按用量等于该泉长期均值估算。地格排放量未计量。 |

「缺口」按可用库存 ÷ 理论净缺口排序。含跨周期均值时，点来源查看阶段或分段预测。有未量化产出时，净额和库存可撑周期待确认。 [中文 Wiki·小动物](https://oxygennotincluded.wiki.gg/zh/wiki/小动物) · [中文 Wiki·间歇泉](https://oxygennotincluded.wiki.gg/zh/wiki/间歇泉)

理论在打开面板、周期结转或按「刷新」时重算。悬停周期标签可查快照时间。实测覆盖已接入的建筑、复制人呼吸和畜舍产蛋。上周期部分覆盖时显示小计和覆盖范围。近 10 周期均值按产耗分算，已测零计入，完整汇总随存档保存。升级后，旧周期继续提供产出及其他实测样本，氧气和污染氧的消耗均值从含呼吸计量的周期开始。3 秒明细留在当前会话。农田采收、呼出二氧化碳、野生动植物、捕食和腐坏未接入实测。

## 机制调研

2026-11-20，以 U60（764025）的游戏实现为依据，通过 Wiki、Klei 更新公告和社区算例交叉核对公式及适用条件。

| 主题 | 证据与取舍 |
| --- | --- |
| 供氧与用水 | 游戏的实际呼吸结算携带气体种类与质量，服装和仿生氧气罐进入同一回调。计量按吸入时所在星球归属。理论规划可用默认复制人 60 kg 氧气/周期和电解产率 0.888，10 人满载等效用水约 676 kg/周期。 [复制人耗氧](https://oxygennotincluded.wiki.gg/wiki/Guide/Oxygen) · [电解器](https://oxygennotincluded.wiki.gg/wiki/Electrolyzer) |
| 涡轮发电与散热 | U60 的 `SteamTurbine.EnergySim200ms` 在蒸汽储量超过 0.1 kg 后才结算，并逐步限制电量。单口 0.4 kg/s 每步进 0.08 kg，稳定工况的平均功率上限约 425 W。明细分别显示蒸汽冷却热流、机体散热需求与发电估算。 [中文 Wiki·涡轮](https://oxygennotincluded.wiki.gg/zh/wiki/蒸汽涡轮机) · [Klei 论坛·涡轮版本讨论](https://kleiforums.com/forums/topic/119874-steam-turbine-setups-post-temp-swap-bug-fix/) |
| 间歇泉 | `GeyserInstanceConfiguration` 区分喷发流量、喷发间歇和休眠占比。活跃期均值用于比较同一泉的运行负载，长期均值用于供给预算，休眠缓冲按停供期间的净需求计算。喷口原生状态不足以证明每格的实际排放。 [间歇泉机制](https://oxygennotincluded.wiki.gg/wiki/Geysers) · [Klei·地质调谐与分析](https://kleiforums.com/forums/topic/145229-game-update-hot-shots-535720-535842/) |
| 畜舍 | U60 的 `OvercrowdingMonitor` 对房内的蛋使用 `FutureOvercrowded` 繁殖效果。明细区分狭窄与封闭，预测取游戏实例的产蛋进度和条件。ONI Database 的成年、定食、标准代谢场景可用作对照，上线数值按实际存档。U60 已在各版本加入水生梳理站，水生动物的可用水域和房间总面积仍须区分。 [小动物状态](https://oxygennotincluded.wiki.gg/zh/wiki/小动物/状态) · [Klei 论坛·蛋与拥挤](https://kleiforums.com/forums/topic/112181-fully-automated-dense-puft-ranching-chickenmadness-based/) · [ONI Database](https://onidb.wiki/tools/ranch-planner) · [Klei U60 更新](https://kleiforums.com/game-updates/oni-alpha/763796-r2847/) |
| 农业与食物 | U60 的 `Crop.SpawnAndGetConfiguredFruit` 将配方数量乘以 `YieldFactor`。关自动采收时，读 `Growing` 的原生老化量和速率折算自然掉果，未知则列为条件。保鲜指南区分冷藏、深冷和气氛。热量库存是快照，腐败未接入实测。 [农业](https://oxygennotincluded.wiki.gg/wiki/Guide/Agriculture) · [Klei·食物保鲜机制](https://kleiforums.com/forums/topic/131854-a-breath-of-fresh-air-update-available-now-471618/) |
| 工业排单 | `Workable.GetEfficiencyMultiplier` 合计当前工人修正、适用的照明和实验室条件及支援建筑，并应用下限。插件只读取这些项。未分配工人时使用已工作的同类设备的工人样本中位数。产量仍取决于送货、缺料和班次。 [Klei U60·支援建筑](https://kleiforums.com/game-updates/oni-alpha/763796-r2847/) |
| 卫生回水、管网与配电 | U60 马桶配方为 5 kg 进水加 6.7 kg 排泄量。净水器可做回水，滤后水仍可能带病菌。气管与液管满包分别为 1 kg、10 kg。管网能力和导线并发 W 写入指南，收支表继续按材料流量和周期能量统计。 [马桶](https://oxygennotincluded.wiki.gg/wiki/Lavatory) · [净水器](https://oxygennotincluded.wiki.gg/wiki/Water_Sieve) · [管网](https://oxygennotincluded.wiki.gg/wiki/Piping) · [电路](https://oxygennotincluded.wiki.gg/wiki/Guide/Power_Circuits) |

## 安装和构建

Windows 用户下载 `OniResourceAnalysis-1.0.1-U60.zip` 并完整解压。退出游戏后双击 `setup.cmd`，安装器会放置并启用模组，升级时会备份旧版。手动安装可将解压后的 `OniResourceBalance` 文件夹放入游戏的 `mods/Local`，然后在主菜单的「模组」中启用。[各平台模组目录](https://github.com/peterhaneve/ONIMods#local-downloads)

自行构建使用 PowerShell 7.4+ 和本机《缺氧》安装目录的 `OxygenNotIncluded_Data/Managed`。`./publish-local.ps1 -Archive <ZIP>` 将安装包解压至 `dist/local-package/OniResourceBalance` 并安装启用，可用 `-PackageDirectory` 指定解压位置。

```powershell
& ./build.ps1 -ManagedDir '游戏目录/OxygenNotIncluded_Data/Managed'
```

产物位于 `dist/OniResourceBalance`。[Klei 模组清单](https://kleiforums.com/forums/topic/158363-setting-up-mod_infoyaml-and-archived_versions/)

## 验证

- `v1.0.0` 通过 Steam U60（764025）客户端的隔离测试。载入旧档后，从游戏资源区打开收支面板，按星球生成结果并打开指南。测试副本将玩家数据路径指向临时目录。
- U60（764025）的隔离旧档回归覆盖了历史记录读取、作物产量倍率与自然掉果、加工工速、两种呼吸气体分别计量和喷口休眠缓冲。受控涡轮算例得到单口 425 W、满流 850 W。长期产量、涡轮持续工况和帧率待验证。

## 许可证与版权

原创代码、安装脚本及文档采用 MIT 许可证，版权署名为 `luo1631`。第三方依赖以及《缺氧》相关权利见版权说明。[MIT 许可证](LICENSE) · [版权与第三方说明](NOTICE.md)
