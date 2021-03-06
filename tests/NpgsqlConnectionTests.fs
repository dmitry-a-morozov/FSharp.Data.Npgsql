module NpgsqlConnectionTests

open System
open Xunit
open Microsoft.Extensions.Configuration
open FSharp.Data.Npgsql

[<Literal>]
let config = __SOURCE_DIRECTORY__ + "/" + "development.settings.json"

[<Literal>]
let connectionStringName ="dvdRental"

let dvdRental = lazy ConfigurationBuilder().AddJsonFile(config).Build().GetConnectionString(connectionStringName)

let openConnection() = 
    let conn = new Npgsql.NpgsqlConnection(dvdRental.Value)
    conn.Open()
    conn

type DvdRental = NpgsqlConnection<connectionStringName, Config = config>

[<Fact>]
let selectLiterals() =
    use cmd = 
        DvdRental.CreateCommand<"        
            SELECT 42 AS Answer, current_date as today
        ">(dvdRental.Value)

    let x = cmd.Execute() |> Seq.exactlyOne
    Assert.Equal(Some 42, x.answer)
    Assert.Equal(Some DateTime.UtcNow.Date, x.today)

[<Fact>]
let selectSingleRow() =
    use cmd = DvdRental.CreateCommand<"        
        SELECT 42 AS Answer, current_date as today
    ", SingleRow = true>(dvdRental.Value)

    Assert.Equal(
        Some( Some 42, Some DateTime.UtcNow.Date), 
        cmd.Execute() |> Option.map ( fun x ->  x.answer, x.today )
    )

[<Fact>]
let selectTuple() =
    use cmd = DvdRental.CreateCommand<"    
        SELECT 42 AS Answer, current_date as today
    ", ResultType.Tuples>(dvdRental.Value)

    Assert.Equal<_ list>(
        [ Some 42, Some DateTime.UtcNow.Date ],
        cmd.Execute() |>  Seq.toList
    )

[<Fact>]
let selectSingleNull() =
    use cmd = DvdRental.CreateCommand<"SELECT NULL", SingleRow = true>(dvdRental.Value)
    Assert.Equal(Some None, cmd.Execute())

[<Fact>]
let selectSingleColumn() =
    use cmd = DvdRental.CreateCommand<"SELECT * FROM generate_series(0, 10)">(dvdRental.Value)

    Assert.Equal<_ seq>(
        { 0 .. 10 }, 
        cmd.Execute() |> Seq.choose id 
    )

[<Fact>]
let paramInFilter() =
    use cmd = 
        DvdRental.CreateCommand<"
            SELECT * FROM generate_series(0, 10) AS xs(value) WHERE value % @div = 0
        ">(dvdRental.Value)

    Assert.Equal<_ seq>(
        { 0 .. 2 .. 10 }, 
        cmd.Execute(div = 2) |> Seq.choose id 
    )

[<Fact>]
let paramInLimit() =
    use cmd = 
        DvdRental.CreateCommand<"
            SELECT * FROM generate_series(0, 10) LIMIT @limit
        ">(dvdRental.Value)

    let limit = 5
    Assert.Equal<_ seq>(
        { 0 .. 10 } |> Seq.take limit , 
        cmd.Execute(int64 limit) |> Seq.choose id
    )

[<Literal>]
let getRentalById = "SELECT return_date FROM rental WHERE rental_id = @id"

[<Fact>]
let dateTableWithUpdate() =

    let rental_id = 2

    use cmd = 
        DvdRental.CreateCommand<"
            SELECT * FROM rental WHERE rental_id = @rental_id
        ", ResultType.DataTable>(dvdRental.Value) 
        
    let t = cmd.Execute(rental_id)
    Assert.Equal(1, t.Rows.Count)
    let r = t.Rows.[0]
    let return_date = r.return_date
    let rowsAffected = ref 0
    try
        let new_return_date = Some DateTime.Now.Date
        r.return_date <- new_return_date
        rowsAffected := t.Update(dvdRental.Value)
        Assert.Equal(1, !rowsAffected)

        use cmd = DvdRental.CreateCommand<getRentalById>(dvdRental.Value)
        Assert.Equal( new_return_date, cmd.Execute( rental_id) |> Seq.exactlyOne ) 

    finally
        if !rowsAffected = 1
        then 
            r.return_date <- return_date
            t.Update(dvdRental.Value) |>  ignore      
            
[<Fact>]
let dateTableWithUpdateAndTx() =
    
    let rental_id = 2
    
    use conn = openConnection()
    use tran = conn.BeginTransaction()

    use cmd = 
        DvdRental.CreateCommand<"SELECT * FROM rental WHERE rental_id = @rental_id", ResultType.DataTable, XCtor = true>(conn, tran)    
    let t = cmd.Execute(rental_id)
    Assert.Equal(1, t.Rows.Count)
    let r = t.Rows.[0]
    let return_date = r.return_date

    let new_return_date = Some DateTime.Now.Date
    r.return_date <- new_return_date
    Assert.Equal(1, t.Update(conn, transaction = tran))

    use getRentalByIdCmd = DvdRental.CreateCommand<getRentalById, XCtor = true>(conn, tran)
    Assert.Equal( 
        new_return_date, 
        getRentalByIdCmd.Execute( rental_id) |>  Seq.exactlyOne
    ) 

    tran.Rollback()

    Assert.Equal(
        return_date, 
        DvdRental.CreateCommand<getRentalById>(dvdRental.Value).Execute( rental_id) |> Seq.exactlyOne
    ) 

[<Fact>]
let dateTableWithUpdateWithConflictOptionCompareAllSearchableValues() =
    
    let rental_id = 2
    
    use conn = openConnection()
    use tran = conn.BeginTransaction()

    use cmd = 
        DvdRental.CreateCommand<"
            SELECT * FROM rental WHERE rental_id = @rental_id
        ", ResultType.DataTable, XCtor = true>(conn, tran)    
  
    let t = cmd.Execute(rental_id)

    [ for c in t.Columns ->  c.ColumnName, c.DataType, c.DateTimeMode  ] |> printfn "\nColumns:\n%A"

    Assert.Equal(1, t.Rows.Count)
    let r = t.Rows.[0]
    r.return_date <- r.return_date |> Option.map (fun d -> d.AddDays(1.))
    //Assert.Equal(1, t.Update(connection = conn, transaction = tran, conflictOption = Data.ConflictOption.CompareAllSearchableValues ))
    Assert.Equal(1, t.Update(conn, tran, conflictOption = Data.ConflictOption.OverwriteChanges ))
     
    use getRentalByIdCmd = DvdRental.CreateCommand<getRentalById, XCtor = true>(conn, tran)
    Assert.Equal( 
        r.return_date, 
        getRentalByIdCmd.Execute( rental_id) |>  Seq.exactlyOne 
    ) 

[<Fact>]
let deleteWithTx() =
    let rental_id = 2

    use cmd = DvdRental.CreateCommand<getRentalById>(dvdRental.Value)
    Assert.Equal(1, cmd.Execute( rental_id) |> Seq.length) 

    do 
        use conn = openConnection()
        use tran = conn.BeginTransaction()

        use del = 
            DvdRental.CreateCommand<"
                DELETE FROM rental WHERE rental_id = @rental_id
            ", XCtor = true>(conn, tran)  
        Assert.Equal(1, del.Execute(rental_id))
        Assert.Empty( DvdRental.CreateCommand<getRentalById, XCtor = true>(conn, tran).Execute( rental_id)) 


    Assert.Equal(1, cmd.Execute( rental_id) |> Seq.length) 
    
type Rating = DvdRental.``public``.Types.mpaa_rating

[<Fact>]
let selectEnum() =
    use cmd = 
        DvdRental.CreateCommand<"
            SELECT * 
            FROM UNNEST( enum_range(NULL::mpaa_rating)) AS X 
            WHERE X <> @exclude;          
        ">(dvdRental.Value)
    Assert.Equal<_ list>(
        [ Rating.G; Rating.PG; Rating.R; Rating.``NC-17`` ],
        [ for x in cmd.Execute(exclude = Rating.``PG-13``) -> x.Value ]
    ) 

////ALTER TABLE public.country ADD ratings MPAA_RATING[] NULL;

[<Fact>]
let selectEnumWithArray() =
    use cmd = DvdRental.CreateCommand<"
        SELECT COUNT(*)  FROM film WHERE ARRAY[rating] <@ @xs::text[]::mpaa_rating[];
    ", SingleRow = true>(dvdRental.Value)

    Assert.Equal( Some( Some 223L), cmd.Execute([| "PG-13" |])) 

[<Fact>]
let allParametersOptional() =
    let cmd = 
        DvdRental.CreateCommand<"
            SELECT coalesce(@x, 'Empty') AS x
        ", AllParametersOptional = true, SingleRow = true>(dvdRental.Value)
    Assert.Equal(Some( Some "test"), cmd.Execute(Some "test")) 
    Assert.Equal(Some( Some "Empty"), cmd.Execute()) 

[<Fact>]
let tableInsert() =
    
    let rental_id = 2
    
    use cmd = DvdRental.CreateCommand<"SELECT * FROM rental WHERE rental_id = @rental_id", SingleRow = true>(dvdRental.Value)  
    let x = cmd.AsyncExecute(rental_id) |> Async.RunSynchronously |> Option.get
        
    use conn = openConnection()
    use tran = conn.BeginTransaction()
    use t = new DvdRental.``public``.Tables.rental()
    let r = 
        t.NewRow(
            staff_id = x.staff_id, 
            customer_id = x.customer_id, 
            inventory_id = x.inventory_id, 
            rental_date = x.rental_date.AddDays(1.), 
            return_date = x.return_date
        )

    t.Rows.Add(r)
    Assert.Equal(1, t.Update(conn, tran))
    let y = 
        use cmd = DvdRental.CreateCommand<"SELECT * FROM rental WHERE rental_id = @rental_id", SingleRow = true, XCtor = true>(conn, tran)
        cmd.Execute(r.rental_id) |> Option.get

    Assert.Equal(x.staff_id, y.staff_id)
    Assert.Equal(x.customer_id, y.customer_id)
    Assert.Equal(x.inventory_id, y.inventory_id)
    Assert.Equal(x.rental_date.AddDays(1.), y.rental_date)
    Assert.Equal(x.return_date, y.return_date)

    tran.Rollback()

    Assert.Equal(None, cmd.Execute(r.rental_id))

[<Fact>]
let tableInsertViaAddRow() =
    
    let rental_id = 2
    
    use cmd = DvdRental.CreateCommand<"SELECT * FROM rental WHERE rental_id = @rental_id", SingleRow = true>(dvdRental.Value)  
    let x = cmd.AsyncExecute(rental_id) |> Async.RunSynchronously |> Option.get
        
    use conn = openConnection()
    use tran = conn.BeginTransaction()
    use t = new DvdRental.``public``.Tables.rental()

    t.AddRow(
        staff_id = x.staff_id, 
        customer_id = x.customer_id, 
        inventory_id = x.inventory_id, 
        rental_date = x.rental_date.AddDays(1.), 
        return_date = x.return_date
    )

    let r = t.Rows.[t.Rows.Count - 1]

    Assert.Equal(1, t.Update(connection = conn, transaction = tran))
    let y = 
        use cmd = DvdRental.CreateCommand<"SELECT * FROM rental WHERE rental_id = @rental_id", SingleRow = true, XCtor = true>(conn, tran)
        cmd.Execute(r.rental_id) |> Option.get

    Assert.Equal(x.staff_id, y.staff_id)
    Assert.Equal(x.customer_id, y.customer_id)
    Assert.Equal(x.inventory_id, y.inventory_id)
    Assert.Equal(x.rental_date.AddDays(1.), y.rental_date)
    Assert.Equal(x.return_date, y.return_date)

    tran.Rollback()

    Assert.Equal(None, cmd.Execute(r.rental_id))
[<Fact>]
let selectEnumWithArray2() =
    use cmd = DvdRental.CreateCommand<"SELECT @ratings::mpaa_rating[];", SingleRow = true>(dvdRental.Value)

    let ratings = [| 
        DvdRental.``public``.Types.mpaa_rating.``PG-13``
        DvdRental.``public``.Types.mpaa_rating.R
    |]
        
    Assert.Equal( Some(  Some ratings), cmd.Execute(ratings))

[<Fact>]
let selectLiteralsWithConnObject() =
    use cmd = 
        DvdRental.CreateCommand<"SELECT 42 AS Answer, current_date as today", XCtor = true>( NpgsqlCmdTests.openConnection())

    let x = cmd.Execute() |> Seq.exactlyOne
    Assert.Equal(Some 42, x.answer) 
    Assert.Equal(Some DateTime.UtcNow.Date, x.today)


type DvdRentalWithConn = NpgsqlConnection<NpgsqlCmdTests.dvdRental, XCtor = true>

[<Fact>]
let selectLiteralsWithConnObjectGlobalSet() =
    use cmd = 
        DvdRentalWithConn.CreateCommand<"SELECT 42 AS Answer, current_date as today">( NpgsqlCmdTests.openConnection())

    let x = cmd.Execute() |> Seq.exactlyOne
    Assert.Equal(Some 42, x.answer) 
    Assert.Equal(Some DateTime.UtcNow.Date, x.today)

type DvdRentalForScripting = NpgsqlConnection<NpgsqlCmdTests.dvdRental, Fsx = true>

[<Fact>]
let fsx() =
    let why = Assert.Throws<exn>(fun()  -> 
        use cmd = DvdRentalForScripting.CreateCommand<"SELECT 42 AS Answer">()        
        cmd.Execute() |> ignore
    )
    Assert.Equal(
        "Design-time connection string re-use allowed at run-time only when executed inside FSI.",
        why.Message
    )

[<Fact>]
let ``AddRow/NewRow preserve order``() =
    let actors = new DvdRental.``public``.Tables.actor()
    let r = actors.NewRow(Some 42, "Tom", "Hanks", Some DateTime.Now)
    actors.Rows.Add(r); actors.Rows.Remove(r) |> ignore
    let r = actors.NewRow(actor_id = Some 42, first_name = "Tom", last_name = "Hanks", last_update = Some DateTime.Now)
    actors.Rows.Add(r); actors.Rows.Remove(r) |> ignore
    
    actors.AddRow(first_name = "Tom", last_name = "Hanks", last_update = Some DateTime.Now)
    actors.AddRow(last_update = Some DateTime.Now, first_name = "Tom", last_name = "Hanks")

    let films = new DvdRental.``public``.Tables.film()
    films.AddRow(
        title = "Inception", 
        description = Some "A thief, who steals corporate secrets through the use of dream-sharing technology, is given the inverse task of planting an idea into the mind of a CEO.",
        language_id = 1s,
        fulltext = NpgsqlTypes.NpgsqlTsVector(ResizeArray())
    )

[<Fact>]
let Add2Rows() =
    use conn = openConnection()
    use tx = conn.BeginTransaction()
    let actors = new DvdRental.``public``.Tables.actor()
    actors.AddRow(first_name = "Tom", last_name = "Hanks")
    actors.AddRow(first_name = "Tom", last_name ="Cruise", last_update = Some DateTime.Now)
    let i = actors.Update(conn, tx)
    Assert.Equal(actors.Rows.Count, i)

    