namespace FSharpBot

open System
open System.Threading.Tasks
open System.Threading
open System.Text
open System.Web

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

open NetCord
open NetCord.Rest

open FSharp.Data
open LiteDB

type PlatformType =
    | Reddit
    | Discourse

type PollData =
    { RedditId: string option
      RedditCommentId: string option
      DiscourseId: int option }

type Update =
    { Platform: PlatformType
      Id: string
      TopicId: string
      TopicTitle: string
      Url: string
      Time: DateTimeOffset
      Author: string
      AuthorUrl: string
      AuthorImage: string option
      Text: string option
      Link: string option
      Image: string option }

type DBTopic =
    { TopicKey: string
      ThreadId: int64
      Updates: System.Collections.Generic.List<string> (* LiteDB doesn't handle FSharpList`1 correctly *) }

type RedditAuth =
    { Token: string
      Expire: DateTimeOffset }

type RedditAvatar = { Username: string; Avatar: string }

type RedditPostResponse =
    JsonProvider<"""
{
  "data": {
    "children": [
      {
        "data": {
          "selftext": "",
          "selftext_html": null,
          "url": "https://example.com",
          "url_overridden_by_dest": "https://example.com",
          "permalink": "/r/example/comments/123/title/",
          "author": "username",
          "thumbnail": "self",
          "id": "abc123",
          "created_utc": 1234567890,
          "title": "Example Title"
        }
      }
    ]
  }
}
""">

type RedditCommentResponse =
    JsonProvider<"""
{
  "data": {
    "children": [
      {
        "data": {
          "id": "def456",
          "link_title": "Example Link Title",
          "link_id": "t3_abc123",
          "body": "Comment body",
          "author": "commenter",
          "permalink": "/r/example/comments/123/title/456/",
          "created_utc": 1234567890
        }
      }
    ]
  }
}
""">

type RedditUserResponse =
    JsonProvider<"""
{
  "data": {
    "icon_img": "https://example.com/avatar.jpg"
  }
}
""">

type RedditTokenResponse =
    JsonProvider<"""
{
  "access_token": "token123",
  "expires_in": 3600
}
""">

type DiscourseResponse =
    JsonProvider<"""
{
  "latest_posts": [
    {
      "id": 123,
      "topic_id": 456,
      "topic_title": "Example Topic",
      "created_at": "2023-01-01T00:00:00.000Z",
      "cooked": "<p>Post content</p>",
      "post_url": "/t/example/456/1",
      "name": "Full Name",
      "username": "username",
      "avatar_template": "/user_avatar/site/username/{size}/1.png"
    }
  ]
}
""">

type ContentUpdater(logger: ILogger, config: ContentPollingConfiguration, database: LiteDatabase, client: RestClient) =
    let markdownConverter =
        ReverseMarkdown.Converter(
            ReverseMarkdown.Config(
                UnknownTags = ReverseMarkdown.Config.UnknownTagsOption.Drop,
                SmartHrefHandling = true
            )
        )

    let mutable redditAuth: RedditAuth option = None

    let htmlDecode (text: string) =
        (HttpUtility.HtmlDecode(text) |> Option.ofObj).Value

    let htmlToMarkdown (html: string) = markdownConverter.Convert(html)

    let abbreviate (text: string) (maxLength: int) =
        if text.Length > maxLength then
            text.Substring(0, maxLength - 3) + "..."
        else
            text

    let badHash (input: string) =
        let m = 13L
        let mutable d = 7L
        let mutable p = 1L

        for c in input do
            p <- p * 2L
            d <- (int64 c * p + d) % m

        d

    let getRedditAuth (config: RedditConfiguration) (force: bool) =
        task {
            let now = DateTimeOffset.UtcNow

            match redditAuth with
            | Some auth when not force && now < auth.Expire -> return auth.Token
            | _ ->
                let credentials =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.ClientId}:{config.ClientSecret}"))

                let! response =
                    Http.AsyncRequestString(
                        url = $"{config.BaseUrl}/api/v1/access_token",
                        httpMethod = "POST",
                        headers =
                            [ "Authorization", $"Basic {credentials}"
                              "User-Agent", config.UserAgent
                              "Content-Type", "application/x-www-form-urlencoded" ],
                        body =
                            TextRequest
                                $"grant_type=password&username={Uri.EscapeDataString config.Username}&password={Uri.EscapeDataString config.Password}"
                    )

                let tokenData = RedditTokenResponse.Parse(response)

                let auth =
                    { Token = tokenData.AccessToken
                      Expire = now.AddSeconds(tokenData.ExpiresIn) }

                redditAuth <- Some auth
                logger.LogInformation("Authenticated to Reddit")
                return auth.Token
        }

    let getRedditAvatar (config: RedditConfiguration) (username: string) =
        task {
            if username = "[deleted]" then
                return None
            else
                let collection = database.GetCollection<RedditAvatar>("redditAvatars")

                let cached = collection.FindOne(Query.EQ("Username", username))

                match Option.ofObj cached with
                | Some result -> return Some result.Avatar
                | None ->
                    try
                        let! token = getRedditAuth config false

                        let! response =
                            Http.AsyncRequestString(
                                url = $"{config.OAuthUrl}/u/{username}/about.json",
                                headers = [ "Authorization", $"Bearer {token}"; "User-Agent", config.UserAgent ]
                            )

                        let userData = RedditUserResponse.Parse(response)
                        let avatar = htmlDecode (userData.Data.IconImg)
                        let redditAvatar = { Username = username; Avatar = avatar }
                        collection.Insert(redditAvatar) |> ignore
                        return Some avatar
                    with ex ->
                        logger.LogWarning("Failed to get Reddit avatar for {Username}: {Error}", username, ex.Message)
                        return None
        }

    let fetchRedditPosts (config: RedditConfiguration) (lastId: string option) =
        task {
            let! token = getRedditAuth config false

            let query =
                match lastId with
                | Some id -> $"?before=t3_{id}"
                | None -> ""

            let! response =
                Http.AsyncRequestString(
                    url = $"{config.OAuthUrl}/r/{config.SubredditName}/new.json{query}",
                    headers = [ "Authorization", $"Bearer {token}"; "User-Agent", config.UserAgent ]
                )

            return RedditPostResponse.Parse(response)
        }

    let fetchRedditComments (config: RedditConfiguration) (lastId: string option) =
        task {
            let! token = getRedditAuth config false

            let query =
                match lastId with
                | Some id -> $"?before=t1_{id}"
                | None -> ""

            let! response =
                Http.AsyncRequestString(
                    url = $"{config.OAuthUrl}/r/{config.SubredditName}/comments.json{query}",
                    headers = [ "Authorization", $"Bearer {token}"; "User-Agent", config.UserAgent ]
                )

            return RedditCommentResponse.Parse(response)
        }

    let fetchDiscoursePosts (config: DiscourseConfiguration) =
        task {
            let! response =
                Http.AsyncRequestString(
                    url = $"{config.BaseUrl}/posts.json",
                    headers = [ "User-Agent", config.UserAgent ]
                )

            return DiscourseResponse.Parse(response)
        }

    let createRedditUpdate (config: RedditConfiguration) (post: RedditPostResponse.Data2) =
        task {
            let! avatar = getRedditAvatar config post.Author

            return
                { Platform = Reddit
                  Id = $"t3_{post.Id}"
                  TopicId = $"t3_{post.Id}"
                  TopicTitle = htmlDecode post.Title
                  Url = $"{config.BaseUrl}{post.Permalink}"
                  Time = DateTimeOffset.FromUnixTimeSeconds(post.CreatedUtc)
                  Author = post.Author
                  AuthorUrl = $"{config.BaseUrl}/u/{post.Author}"
                  AuthorImage = avatar
                  Text =
                    let selftext = post.Selftext.JsonValue.AsString() // 'string ...' results in "null" for an empty value

                    if String.IsNullOrEmpty(selftext) then
                        None
                    else
                        Some(htmlDecode (selftext))
                  Link =
                    match post.UrlOverriddenByDest with
                    | null -> None
                    | url -> Some url
                  Image =
                    let thumbnail = post.Thumbnail

                    if thumbnail = "self" || thumbnail = "default" then
                        None
                    else
                        Some thumbnail }
        }

    let createRedditCommentUpdate (config: RedditConfiguration) (comment: RedditCommentResponse.Data2) =
        task {
            let! avatar = getRedditAvatar config comment.Author

            return
                { Platform = Reddit
                  Id = $"t1_{comment.Id}"
                  TopicId = comment.LinkId
                  TopicTitle = htmlDecode comment.LinkTitle
                  Url = $"{config.BaseUrl}{comment.Permalink}"
                  Time = DateTimeOffset.FromUnixTimeSeconds(comment.CreatedUtc)
                  Author = comment.Author
                  AuthorUrl = $"{config.BaseUrl}/u/{comment.Author}"
                  AuthorImage = avatar
                  Text = Some comment.Body
                  Link = None
                  Image = None }
        }

    let createDiscourseUpdate (config: DiscourseConfiguration) (post: DiscourseResponse.LatestPost) =
        let avatarUrl = post.AvatarTemplate.Replace("{size}", "128")

        { Platform = Discourse
          Id = string post.Id
          TopicId = string post.TopicId
          TopicTitle = post.TopicTitle
          Url = $"{config.BaseUrl}{post.PostUrl}"
          Time = post.CreatedAt
          Author =
            let name = post.Name

            if String.IsNullOrEmpty(name) then post.Username else name
          AuthorUrl = $"{config.BaseUrl}/u/{post.Username}"
          AuthorImage =
            if Uri.IsWellFormedUriString(avatarUrl, UriKind.Absolute) then
                Some avatarUrl
            else
                Some $"{config.BaseUrl}{avatarUrl}"
          Text = Some(htmlToMarkdown post.Cooked)
          Link = None
          Image = None }

    let createEmbeds (update: Update) =
        let userColors =
            [| 0xb366ff
               0xff6666
               0x66b3ff
               0xffcc66
               0x66ffb3
               0xff66b3
               0xb3ff66
               0x66ffcc
               0xcc66ff
               0x66b3cc |]

        let colorIndex = (badHash update.AuthorUrl % userColors.LongLength) |> int
        let color = userColors[colorIndex]

        let platformName =
            match update.Platform with
            | Reddit -> "Reddit"
            | Discourse -> "Discourse"

        let text = update.Text |> Option.defaultValue ""
        let contentText = $"**(from [{platformName}]({update.Url}))**\n\n{text}"
        let content = abbreviate (contentText.Trim()) (4096 * 5)

        let embeds =
            let step = 4096 - 3

            [ 0..step .. content.Length - 1 ]
            |> List.mapi (fun i start ->
                let isLast = start + step >= content.Length

                let chunk =
                    if isLast then
                        content.Substring(start)
                    else
                        content.Substring(start, step) + "..."

                let title =
                    if i > 0 then
                        $"(Cont.) {update.TopicTitle}"
                    else
                        update.TopicTitle

                EmbedProperties(
                    Color = Color(color),
                    Title = abbreviate title 256,
                    Author =
                        EmbedAuthorProperties(
                            Name = update.Author,
                            Url = update.AuthorUrl,
                            IconUrl =
                                match update.AuthorImage with
                                | Some url -> url
                                | None -> null
                        ),
                    Timestamp = update.Time,
                    Description = chunk,
                    Url =
                        (match update.Link with
                         | Some url -> url
                         | None -> null),
                    Image =
                        match update.Image with
                        | Some img -> img
                        | None -> null
                ))

        embeds

    member this.Update() =
        task {
            try
                logger.LogInformation("Polling for new content...")

                let collection = database.GetCollection<PollData>("pollData")

                let currentData =
                    collection.FindOne(Query.All())
                    |> Option.ofObj
                    |> Option.defaultValue
                        { RedditId = None
                          RedditCommentId = None
                          DiscourseId = None }

                let! redditPosts = fetchRedditPosts config.Reddit currentData.RedditId

                let! redditComments = fetchRedditComments config.Reddit currentData.RedditCommentId

                let! discoursePosts = fetchDiscoursePosts config.Discourse

                let filteredDiscoursePosts =
                    match currentData.DiscourseId with
                    | Some lastId -> discoursePosts.LatestPosts |> Array.filter (fun p -> p.Id > lastId)
                    | None -> discoursePosts.LatestPosts

                let redditPostUpdates = Array.zeroCreate redditPosts.Data.Children.Length

                for i = 0 to redditPosts.Data.Children.Length - 1 do
                    let! update = createRedditUpdate config.Reddit redditPosts.Data.Children[i].Data
                    redditPostUpdates[i] <- update

                let redditCommentUpdates = Array.zeroCreate redditComments.Data.Children.Length

                for i = 0 to redditComments.Data.Children.Length - 1 do
                    let! update = createRedditCommentUpdate config.Reddit redditComments.Data.Children[i].Data
                    redditCommentUpdates[i] <- update

                let discourseUpdates =
                    filteredDiscoursePosts |> Array.map (createDiscourseUpdate config.Discourse)

                let allUpdates =
                    Array.concat [ redditPostUpdates; redditCommentUpdates; discourseUpdates ]
                    |> Array.sortBy (fun u -> u.Time)

                for update in allUpdates do
                    try
                        logger.LogInformation(
                            "Processing update: {UpdateId} from {Platform}",
                            update.Id,
                            update.Platform
                        )

                        let topicKey = $"topic_{update.Platform}_{update.TopicId}"
                        let topicCollection = database.GetCollection<DBTopic>("topics")

                        let existingTopic =
                            topicCollection.FindOne(Query.EQ("TopicKey", topicKey)) |> Option.ofObj

                        let embeds = createEmbeds update
                        let forumChannelId = config.ForumChannelId

                        let tagId =
                            match update.Platform with
                            | Reddit -> config.RedditTagId
                            | Discourse -> config.DiscourseTagId

                        match existingTopic with
                        | None ->
                            let! thread =
                                client.CreateForumGuildThreadAsync(
                                    forumChannelId,
                                    ForumGuildThreadProperties(
                                        abbreviate update.TopicTitle 100,
                                        ForumGuildThreadMessageProperties(Embeds = [| embeds[0] |]),
                                        AppliedTags = [| tagId |]
                                    )
                                )

                            do! Task.Delay(1000) // Wait for the thread to be fully created

                            for embed in embeds |> List.skip 1 do
                                let messageProps = MessageProperties()
                                messageProps.Embeds <- [| embed |]
                                let! _ = thread.SendMessageAsync(messageProps)
                                ()

                            let dbTopic =
                                { TopicKey = topicKey
                                  ThreadId = int64 thread.Id
                                  Updates = System.Collections.Generic.List([ update.Id ]) }

                            topicCollection.Insert(topicKey, dbTopic) |> ignore

                            logger.LogInformation(
                                "Created new thread {ThreadId} for topic {TopicId}",
                                thread.Id,
                                update.TopicId
                            )

                        | Some topic when not (topic.Updates |> List.ofSeq |> List.contains update.Id) ->
                            for embed in embeds do
                                let messageProps = MessageProperties(Embeds = [| embed |])
                                let! _ = client.SendMessageAsync(uint64 topic.ThreadId, messageProps)
                                ()

                            let updatedTopic =
                                { topic with
                                    Updates =
                                        update.Id :: (topic.Updates |> List.ofSeq) |> System.Linq.Enumerable.ToList }

                            topicCollection.Update(topicKey, updatedTopic) |> ignore

                            logger.LogInformation(
                                "Added update {UpdateId} to existing thread {ThreadId}",
                                update.Id,
                                topic.TopicKey
                            )

                        | Some _ -> logger.LogInformation("Update {UpdateId} already exists in thread", update.Id)

                    with ex ->
                        logger.LogError(
                            ex,
                            "Failed to send update {UpdateId} from {Platform}",
                            update.Id,
                            update.Platform
                        )

                let newData =
                    { RedditId =
                        redditPosts.Data.Children
                        |> Array.tryHead
                        |> Option.map (fun child -> child.Data.Id)
                        |> Option.orElse currentData.RedditId

                      RedditCommentId =
                        redditComments.Data.Children
                        |> Array.tryHead
                        |> Option.map (fun child -> child.Data.Id)
                        |> Option.orElse currentData.RedditCommentId

                      DiscourseId =
                        filteredDiscoursePosts
                        |> Array.tryHead
                        |> Option.map (fun post -> post.Id)
                        |> Option.orElse currentData.DiscourseId }

                database.BeginTrans() |> ignore

                collection.DeleteAll() |> ignore
                collection.Insert(newData) |> ignore

                database.Commit() |> ignore

                logger.LogInformation("Content polling completed successfully")

            with ex ->
                logger.LogError(ex, "Error during content polling")
        }

type ContentPollingService
    (
        logger: ILogger<ContentPollingService>,
        options: IOptions<Configuration>,
        database: LiteDatabase,
        client: RestClient
    ) =
    inherit BackgroundService()

    override this.ExecuteAsync(cancellationToken) =
        task {
            let config = options.Value.ContentPolling

            let updater = ContentUpdater(logger, config, database, client)

            use timer = new PeriodicTimer(TimeSpan.FromSeconds(config.IntervalSeconds))

            while true do
                let! result = timer.WaitForNextTickAsync(cancellationToken)

                if not result then
                    return ()

                do! updater.Update()
        }
