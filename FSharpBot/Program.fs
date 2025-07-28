open System
open System.Linq
open System.Threading.Tasks
open System.Runtime.InteropServices

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Microsoft.Extensions.Logging

open NetCord
open NetCord.Rest
open NetCord.Gateway
open NetCord.Services.ApplicationCommands
open NetCord.Hosting.Gateway
open NetCord.Hosting.Services.ApplicationCommands

open LiteDB

open FSharpBot

let host = Host.CreateApplicationBuilder()

host.Services
    .AddScoped<LiteDatabase>(fun _ -> new LiteDatabase("Data.db"))
    .AddDiscordGateway(fun options ->
        options.Intents <- GatewayIntents.Guilds |||
                           GatewayIntents.GuildMessages |||
                           GatewayIntents.GuildUsers |||
                           GatewayIntents.MessageContent
    )
    .AddApplicationCommands()
    .AddGatewayHandler(GatewayEvent.GuildUserUpdate, Func<GuildUser,
                                                          IOptions<Configuration>,
                                                          Task>(fun user options ->
        match user with
        | _ when user.IsBot && user.RoleIds.Contains(options.Value.SpamRoleId) ->
            user.KickAsync(RestRequestProperties().WithAuditLogReason("spam bot role"))
        | _ -> Task.CompletedTask
    ))
    .AddGatewayHandler(GatewayEvent.MessageCreate, Func<Message,
                                                        IOptions<Configuration>,
                                                        ILogger<GatewayClient>,
                                                        IServiceProvider,
                                                        Task>(fun message options logger services ->
        match message.Author with
        | :? GuildUser as user when not user.IsBot ->
            use scope = services.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<LiteDatabase>()

            let collection = db.GetCollection<UserMessageCount>("userMessageCounts")

            let userCount = collection.FindOne(Query.EQ("_id", int64 user.Id)) |> Option.ofObj
            match userCount with
            | None ->
                let newUser = { UserId = int64 user.Id; Count = 1 }
                collection.Insert(newUser) |> ignore
                Task.CompletedTask
            | Some validUserCount ->
                validUserCount.Count <- validUserCount.Count + 1
                let count = validUserCount.Count
                collection.Update(validUserCount) |> ignore
                let activityrole = options.Value.ActivityRoles.FirstOrDefault(fun r -> r.Threshold <= count &&
                                                                                       not(user.RoleIds.Contains(r.Id))) |> Option.ofObj
                match activityrole with
                | None -> Task.CompletedTask
                | Some validActivityRole ->
                    logger.LogInformation("Giving role {RoleId} to user {UserId} for reaching {Count} messages", validActivityRole.Id, user.Id, count)
                    user.AddRoleAsync(validActivityRole.Id)
        | _ -> Task.CompletedTask
    ))
    |> ignore

host.Services
    .AddOptions<Configuration>()
    .BindConfiguration(nameof Configuration)
    |> ignore

let app = host.Build()

type Count = delegate of IOptions<Configuration> *
                         IServiceProvider *
                         ApplicationCommandContext *
                         [<SlashCommandParameter(Description = "User to count messages of");
                           DefaultParameterValue(null: GuildUser | null);
                           Optional>] user: GuildUser | null -> string

app
    .AddSlashCommand("count", "How many messages until you get your next role?", Count(fun options services context user ->
        let targetUserResult =
            match user |> Option.ofObj with
            | None -> match context.User with
                      | :? GuildUser as guildUser -> Ok(guildUser)
                      | _ -> Error "Command must be invoked in a guild or with a user."
            | Some validUser -> Ok(validUser)

        match targetUserResult with
        | Error errorMessage -> errorMessage
        | Ok targetUser ->
            use scope = services.CreateScope()
            let db = scope.ServiceProvider.GetRequiredService<LiteDatabase>()

            let collection = db.GetCollection<UserMessageCount>("userMessageCounts")

            let userCount = collection.FindOne(Query.EQ("_id", int64 targetUser.Id)) |> Option.ofObj
            let count = match userCount with
                        | None -> 0
                        | Some validUserCount -> validUserCount.Count

            let role = options.Value.ActivityRoles.FirstOrDefault(fun r -> r.Threshold > count) |> Option.ofObj
            match role with
            | None -> $"{targetUser} is at {count} messages now! Has reached the top of the ladder buddy."
            | Some validRole -> $"{targetUser} is at {count} messages now! Needs {validRole.Threshold - count} more messages to get the role <@&{validRole.Id}>."
    ))
    .UseGatewayHandlers() |> ignore

app.Run()
