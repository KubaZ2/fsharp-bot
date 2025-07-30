namespace FSharpBot

open System.Collections.Generic

type ActivityRole = { Id: uint64; Threshold: int }

type RedditConfiguration() =
    member val BaseUrl: string = "https://www.reddit.com" with get, set
    member val OAuthUrl: string = "https://oauth.reddit.com" with get, set
    member val SubredditName: string = "fsharp" with get, set
    member val ClientId: string = "" with get, set
    member val ClientSecret: string = "" with get, set
    member val Username: string = "" with get, set
    member val Password: string = "" with get, set
    member val UserAgent: string = "FSharp Discord Bot" with get, set

type DiscourseConfiguration() =
    member val BaseUrl: string = "https://forums.fsharp.org" with get, set
    member val UserAgent: string = "FSharp Discord Bot" with get, set

type ContentPollingConfiguration() =
    member val IntervalSeconds: float = 300 with get, set
    member val Reddit: RedditConfiguration = RedditConfiguration() with get, set
    member val Discourse: DiscourseConfiguration = DiscourseConfiguration() with get, set
    member val ForumChannelId: uint64 = 0uL with get, set
    member val RedditTagId: uint64 = 0uL with get, set
    member val DiscourseTagId: uint64 = 0uL with get, set

type Configuration() =
    member val ActivityRoles: IReadOnlyList<ActivityRole> = [] with get, set
    member val SpamRoleId: uint64 = 0uL with get, set
    member val ContentPolling: ContentPollingConfiguration = ContentPollingConfiguration() with get, set
