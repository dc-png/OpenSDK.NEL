using System.Text.Json;
using Codexus.Cipher.Entities;
using Codexus.Cipher.Entities.WPFLauncher.NetGame;
using Codexus.Cipher.Protocol;
using Codexus.Cipher.Protocol.Registers;
using Codexus.Development.SDK.Entities;
using Codexus.Development.SDK.Manager;
using Codexus.Development.SDK.Utils;
using Codexus.Game.Launcher.Services.Java;
using Codexus.Game.Launcher.Utils;
using Codexus.Interceptors;
using Codexus.OpenSDK;
using Codexus.OpenSDK.Entities.X19;
using Codexus.OpenSDK.Entities.Yggdrasil;
using Codexus.OpenSDK.Generator;
using Codexus.OpenSDK.Http;
using Codexus.OpenSDK.Yggdrasil;
using OpenSDK.NEL;
using OpenSDK.NEL.Entities;
using Serilog;

ConfigureLogger();

Log.Information("* 此软件基于 Codexus.OpenSDK 以及 Codexus.Development.SDK 制作，旨在为您提供更简洁的脱盒体验。");

await InitializeSystemComponentsAsync();

var services = await CreateServices();

await services.X19.InitializeDeviceAsync();

var (authOtp, channel) = await LoginAsync(services);
Log.Information("已登录至用户: {Id}, 渠道: {Channel}", authOtp.EntityId, channel);

while (true)
{
    var selectedServer = await SelectServerAsync(authOtp);
    await ManageServerAsync(authOtp, services, selectedServer);
}

static void ConfigureLogger()
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console()
        .CreateLogger();
}

static async Task InitializeSystemComponentsAsync()
{
    Interceptor.EnsureLoaded();
    PacketManager.Instance.EnsureRegistered();
    PluginManager.Instance.EnsureUninstall();
    PluginManager.Instance.LoadPlugins("plugins");
    await Task.CompletedTask;
}

static async Task<Services> CreateServices()
{
    var api = new WebNexusApi("YXBwSWQ9Q29kZXh1cy5HYXRld2F5LmFwcFNlY3JldD1hN0s5bTJYcUw4YkM0d1ox");
    var register = new Channel4399Register();
    var c4399 = new C4399();
    var x19 = new X19();

    var yggdrasil = new StandardYggdrasil(new YggdrasilData
    {
        LauncherVersion = x19.GameVersion,
        Channel = "netease",
        CrcSalt = await ComputeCrcSalt()
    });

    return new Services(api, register, c4399, x19, yggdrasil);
}

static async Task<(X19AuthenticationOtp, string)> LoginAsync(Services services)
{
    var mode = ReadOption("请选择登录模式：1. Cookie 登录  2. 随机 4399 登录", ["1", "2"]);

    return mode switch
    {
        "1" => await LoginWithCookieAsync(services.X19),
        "2" => await LoginWith4399Async(services),
        _ => throw new ArgumentException($"不支持的登录模式: {mode}")
    };
}

static async Task<(X19AuthenticationOtp, string)> LoginWithCookieAsync(X19 x19)
{
    var cookie = ReadText("输入您的 Cookie: ");
    return await x19.ContinueAsync(cookie);
}

static async Task<(X19AuthenticationOtp, string)> LoginWith4399Async(Services services)
{
    const int maxRetries = 3;

    for (var attempt = 1; attempt <= maxRetries; attempt++)
        try
        {
            Log.Information("正在调用接口获取账户... (尝试 {Count}/{Max})", attempt, maxRetries);

            var user = await services.Register.RegisterAsync(
                services.Api.ComputeCaptchaAsync,
                () => new IdCard
                {
                    Name = Channel4399Register.GenerateChineseName(),
                    IdNumber = Channel4399Register.GenerateRandomIdCard()
                });

            var json = await services.C4399.LoginWithPasswordAsync(user.Account, user.Password);
            return await services.X19.ContinueAsync(json);
        }
        catch (Exception e)
        {
            Log.Error(e, "调用接口获取账户或登录时发生异常 (尝试 {Count}/{Max})", attempt, maxRetries);

            if (attempt == maxRetries)
            {
                Log.Error("达到最大重试次数，登录失败");
                throw;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

    throw new InvalidOperationException("登录失败");
}

static async Task<EntityNetGameItem> SelectServerAsync(X19AuthenticationOtp authOtp)
{
    while (true)
    {
        var serverName = ReadText("搜索服务器: ");
        var servers = await authOtp.Api<EntityNetGameKeyword, Entities<EntityNetGameItem>>(
            "/item/query/search-by-keyword",
            new EntityNetGameKeyword { Keyword = serverName });

        if (servers.Data.Length == 0)
        {
            Log.Warning("未找到服务器，请重新搜索。");
            continue;
        }

        Log.Information("找到以下服务器，请选择要游玩的服务器：");
        for (var i = 0; i < servers.Data.Length; i++)
        {
            var server = servers.Data[i];
            Log.Information("{Index}. {ServerName} (ID: {ServerId})", i + 1, server.Name, server.EntityId);
        }

        var choice = ReadNumberInRange(1, servers.Data.Length, "请输入服务器编号");
        return servers.Data[choice - 1];
    }
}

static async Task ManageServerAsync(X19AuthenticationOtp authOtp, Services services, EntityNetGameItem selectedServer)
{
    while (true)
    {
        var roles = await GetServerRolesAsync(authOtp, selectedServer);
        DisplayRoles(roles);

        var operation = ReadOption("请选择操作：1. 启动代理  2. 添加随机角色  3. 添加指定角色  4. 返回服务器选择", ["1", "2", "3", "4"]);

        switch (operation)
        {
            case "1":
                if (await StartProxyAsync(authOtp, services, selectedServer, roles)) return;

                break;

            case "2":
                await CreateRandomCharacterAsync(authOtp, selectedServer);
                break;

            case "3":
                await CreateNamedCharacterAsync(authOtp, selectedServer);
                break;

            case "4":
                return;

            default:
                Log.Warning("不支持的操作: {Operation}", operation);
                break;
        }
    }
}

static async Task<EntityGameCharacter[]> GetServerRolesAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var roles = await authOtp.Api<EntityQueryGameCharacters, Entities<EntityGameCharacter>>(
        "/game-character/query/user-game-characters",
        new EntityQueryGameCharacters
        {
            GameId = server.EntityId,
            UserId = authOtp.EntityId
        });

    return roles.Data;
}

static void DisplayRoles(EntityGameCharacter[] roles)
{
    if (roles.Length > 0)
    {
        Log.Information("当前角色列表：");
        for (var i = 0; i < roles.Length; i++)
        {
            var character = roles[i];
            Log.Information("{Index}. {CharacterName} (ID: {CharacterGameId})", i + 1, character.Name,
                character.GameId);
        }
    }
    else
    {
        Log.Information("没有找到角色。");
    }
}

static async Task<bool> StartProxyAsync(X19AuthenticationOtp authOtp, Services services,
    EntityNetGameItem selectedServer, EntityGameCharacter[] roles)
{
    if (roles.Length == 0)
    {
        Log.Warning("没有可用角色来启动代理。");
        return false;
    }

    var roleChoice = ReadNumberInRange(1, roles.Length, "请选择角色序号启动代理");
    var selectedCharacter = roles[roleChoice - 1];

    Log.Information("已选择角色 {CharacterName} (ID: {CharacterGameId}) 启动代理",
        selectedCharacter.Name, selectedCharacter.GameId);

    try
    {
        var details = await authOtp.Api<EntityQueryNetGameDetailRequest, Entity<EntityQueryNetGameDetailItem>>(
            "/item-details/get_v2",
            new EntityQueryNetGameDetailRequest { ItemId = selectedServer.EntityId });

        var address = await authOtp.Api<EntityAddressRequest, Entity<EntityNetGameServerAddress>>(
            "/item-address/get",
            new EntityAddressRequest { ItemId = selectedServer.EntityId });

        var version = details.Data!.McVersionList[0];
        var gameVersion = GameVersionUtil.GetEnumFromGameVersion(version.Name);

        var serverModInfo = await InstallerService.InstallGameMods(
            authOtp.EntityId,
            authOtp.Token,
            gameVersion,
            new WPFLauncher(),
            selectedServer.EntityId,
            false);

        var mods = JsonSerializer.Serialize(serverModInfo);

        CreateProxyInterceptor(authOtp, services.Yggdrasil, selectedServer, selectedCharacter, version, address.Data!,
            mods);

        await X19.InterconnectionApi.GameStartAsync(authOtp.EntityId, authOtp.Token, selectedServer.EntityId);
        Log.Information("代理服务器已创建并启动。");

        await Task.Delay(Timeout.Infinite);
        return true;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "启动代理时发生错误");
        return false;
    }
}

static void CreateProxyInterceptor(
    X19AuthenticationOtp authOtp,
    StandardYggdrasil yggdrasil,
    EntityNetGameItem server,
    EntityGameCharacter character,
    dynamic version,
    EntityNetGameServerAddress address,
    string mods)
{
    Interceptor.CreateInterceptor(
        new EntitySocks5 { Enabled = false },
        mods,
        server.EntityId,
        server.Name,
        version.Name,
        address.Ip,
        address.Port,
        character.Name,
        authOtp.EntityId,
        authOtp.Token,
        (Action<string>)YggdrasilCallback);
    return;

    void YggdrasilCallback(string serverId)
    {
        Log.Information("Server ID: {Certification}", serverId);

        var signal = new SemaphoreSlim(0);
        _ = Task.Run(async () =>
        {
            try
            {
                var success = await yggdrasil.JoinServerAsync(new GameProfile
                {
                    GameId = server.EntityId,
                    GameVersion = version.Name,
                    BootstrapMd5 = "2A7A476411A1687A56DC6848829C1AE4",
                    DatFileMd5 = "D285CBF97D9BA30D3C445DBF1C342634",
                    Mods = JsonSerializer.Deserialize<ModList>(mods)!,
                    User = new UserProfile { UserId = int.Parse(authOtp.EntityId), UserToken = authOtp.Token }
                }, serverId);

                if (success.IsSuccess)
                    Log.Information("消息认证成功");
                else
                    Log.Error("消息认证失败: {Error}", success.Error);
            }
            catch (Exception e)
            {
                Log.Error(e, "认证过程中发生异常");
            }
            finally
            {
                signal.Release();
            }
        });

        signal.Wait();
    }
}

static async Task CreateRandomCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var randomName = StringGenerator.GenerateRandomString(12, false);
    await CreateCharacterAsync(authOtp, server, randomName);
    Log.Information("已创建随机角色: {Name}", randomName);
}

static async Task CreateNamedCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server)
{
    var roleName = ReadText("角色名称: ");

    if (string.IsNullOrWhiteSpace(roleName))
    {
        Log.Warning("角色名称不能为空");
        return;
    }

    await CreateCharacterAsync(authOtp, server, roleName);
    Log.Information("已创建角色: {Name}", roleName);
}

static async Task CreateCharacterAsync(X19AuthenticationOtp authOtp, EntityNetGameItem server, string name)
{
    try
    {
        await authOtp.Api<EntityCreateCharacter, JsonElement>(
            "/game-character",
            new EntityCreateCharacter
            {
                GameId = server.EntityId,
                UserId = authOtp.EntityId,
                Name = name
            });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "创建角色失败");
        throw;
    }
}

static async Task<string> ComputeCrcSalt()
{
    Log.Information("正在计算 CRC Salt...");

    var http = new HttpWrapper("https://service.codexus.today",
        options => { options.WithBearerToken("0e9327a2-d0f8-41d5-8e23-233de1824b9a.pk_053ff2d53503434bb42fe158"); });

    var response = await http.GetAsync("/crc-salt");

    var json = await response.Content.ReadAsStringAsync();
    var entity = JsonSerializer.Deserialize<OpenSdkResponse<CrcSalt>>(json);

    if (entity != null) return entity.Data.Salt;

    Log.Error("无法计算出 CrcSalt");
    return string.Empty;
}

static string ReadOption(string prompt, string[] validInputs)
{
    while (true)
    {
        Log.Information(prompt);
        Console.Write("> ");
        var input = Console.ReadLine()?.Trim();

        if (!string.IsNullOrEmpty(input) && validInputs.Contains(input)) return input;

        Log.Warning("输入无效，请重新输入。");
    }
}


static string ReadText(string prompt)
{
    Log.Information(prompt);
    Console.Write("> ");
    return Console.ReadLine()?.Trim() ?? string.Empty;
}

static int ReadNumberInRange(int min, int max, string prompt)
{
    while (true)
    {
        Log.Information("{Prompt} ({Min}-{Max}): ", prompt, min, max);
        Console.Write("> ");
        var input = Console.ReadLine();

        if (int.TryParse(input, out var number) && number >= min && number <= max) return number;

        Log.Warning("输入无效，请输入 {Min} 到 {Max} 之间的数字。", min, max);
    }
}

internal record Services(
    WebNexusApi Api,
    Channel4399Register Register,
    C4399 C4399,
    X19 X19,
    StandardYggdrasil Yggdrasil
);