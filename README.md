# ArenaMatch

ArenaMatch 是 [CS Arena 平台](https://github.com/cubelightt/cs-arena) 使用的 CounterStrikeSharp 比赛插件，负责比赛载入、名单与准备状态、竞技比赛、刀局、暂停、单挑、比赛事件和 Demo 录制。插件通过本机 IPC 与 [CS Arena Agent](https://github.com/cubelightt/cs-arena-agent) 通信。

## 下载或构建

编译版本 `ArenaMatch.dll` 和 SHA-256 校验文件见 [Releases](https://github.com/cubelightt/cs-arena-match/releases)。

从源码构建需要 .NET 8 SDK：

```bash
dotnet build ArenaMatch.csproj -c Release
```

输出文件为 `bin/Release/net8.0/ArenaMatch.dll`。当前版本为 0.6.15，使用 Go 桥协议 v2 和本机 IPC v1。

## 安装

1. 在 CS2 服务端安装 CounterStrikeSharp，并配置 CS Arena Agent 连接平台。
2. 将 `ArenaMatch.dll` 放入实例的 `addons/counterstrikesharp/plugins/ArenaMatch/` 目录；同一实例只启用一个比赛控制插件。
3. 按目标游戏和 CounterStrikeSharp 版本核对 `deployment/gamedata.json`，安装对应的运行资源。
4. 在平台启用 ArenaMatch：设置 `ENABLE_ARENA_MATCH=1`，或用 `ARENA_MATCH_INSTANCES` 指定实例。
5. 重启游戏实例，通过平台创建房间并开始比赛。

部署资源与许可证见 [`deployment/README.md`](deployment/README.md)。比赛配置由平台经桥提供，插件不需要保存平台令牌。

## 玩家命令

| 聊天命令 | 用途 |
|---|---|
| `.ready` | 标记准备 |
| `.stay` / `.switch` | 刀局胜者留边 / 换边 |
| `.p` / `.pause` | 申请战术暂停 |
| `.tech` | 申请技术暂停 |
| `.un` | 队伍投票恢复比赛 |
| `.forceun` | 管理员强制恢复比赛 |
| `.guns` | 单挑模式选枪 |
| `.start` | 有权限的管理员强制开始 |

服务端控制台保留 `css_start`、`css_endmatch` 等管理命令。命令是否可用受比赛阶段、名单、阵营和管理员权限限制。

## 鸣谢

感谢以下开源项目及其贡献者：

| 项目 | 使用或参考方式 |
|---|---|
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | 当前插件使用的 .NET API 与运行框架；`deployment/gamedata.json` 的上游来源 |
| [MatchZy](https://github.com/shobhit-pathak/MatchZy)（历史参考） | 早期赛事方案及兼容接口的参考项目；当前比赛实现由 ArenaMatch 提供，发布包不包含 MatchZy |

上游部署资源的许可证见 [`deployment/README.md`](deployment/README.md)。第三方代码和资源保留各自的许可证，本节鸣谢不替代相应的版权与许可声明。

## 开源协议

本项目自有代码采用 GNU General Public License v3.0 ，详见 [LICENSE](LICENSE)。
