namespace FSharpBot

open System.Collections.Generic

type ActivityRole =
    { Id: uint64
      Threshold: int }

type Configuration() =
    member val ActivityRoles: IReadOnlyList<ActivityRole> = [] with get, set

    member val SpamRoleId: uint64 = 0uL with get, set
