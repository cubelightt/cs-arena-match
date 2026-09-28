# ArenaMatch 部署资源

将 `ArenaMatch.dll` 安装到目标实例的 `addons/counterstrikesharp/plugins/ArenaMatch/` 目录。同一实例只启用一个比赛控制插件；Go 桥需连接 CS Arena 平台，平台需启用对应实例的 ArenaMatch 模式。

`gamedata.json` 是游戏与 CounterStrikeSharp 版本适配输入，安装前应与目标主机的 CounterStrikeSharp 版本核对。所需 CounterStrikeSharp 运行文件由对应发行包提供。

升级前备份已有插件与适配配置，再替换 DLL 并重启实例。回退时恢复原插件和配置。Demo、运行日志及比赛状态目录应保留。

CounterStrikeSharp 随包许可证文本见 [`LICENSE`](LICENSE)、[`LICENSE.GPL3`](LICENSE.GPL3) 和 [`LICENSE.MIT`](LICENSE.MIT)。平台配置与玩家命令见[根 README](../README.md)。
