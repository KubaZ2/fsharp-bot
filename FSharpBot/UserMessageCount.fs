namespace FSharpBot

open LiteDB

type UserMessageCount =
    { [<BsonId>] UserId: int64
      mutable Count: int }
