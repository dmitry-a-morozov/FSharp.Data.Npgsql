open FSharp.Data.Npgsql

[<Literal>]
let connectionString = "Host=localhost;Username=postgres;Database=dvdrental;Port=32768"

[<EntryPoint>]
let main _ =

    //remove comment around ResultType.Tuples to make it fail
    let cmd = new NpgsqlCommand<"SELECT now() as now",connectionString (*, ResultType.Tuples *) >(connectionString)

    cmd.Execute() |> Seq.toList |> List.head |> printfn "Result is: %A"

    0 
