using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

[Collection(MockApiCollection.Name)]
public class OrdersEndToEndTests(MockApiFixture mock) : HostTestBase(mock)
{
    [Fact]
    public async Task List_StatusFilterAndCursorPaging()
    {
        var names = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var (status, response, error) = await Host.Execute(List("ext_order", 25, Where("ext_status", Ops.Eq, "100000000"), token));
            Assert.True(status == 200, error?.Message);
            Assert.All(response!.Rows, r => Assert.Equal("100000000", Value(r, "ext_status")));
            names.AddRange(response.Rows.Select(r => Value(r, "ext_name")!));
            token = response.Next;
            pages++;
        } while (token != null);
        Assert.Equal(40, names.Count);
        Assert.Equal(40, names.Distinct().Count());
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task List_RowIdsAreReversibleKeys()
    {
        var (_, response, _) = await Host.Execute(List("ext_order", 3));
        Assert.Equal(Id("ord_0001"), response!.Rows[0].Id);
    }

    // The Web API's PATCH (upsert) and other platform lookups query a virtual table by primary key
    // through RetrieveMultiple (phase 1 findings). The host answers with the Retrieve operation.
    [Fact]
    public async Task List_PrimaryKeyEquality_RoutesToRetrieve()
    {
        var (status, response, error) = await Host.Execute(List("ext_order", 50, Where("ext_orderid", Ops.Eq, Id("ord_0007"))));
        Assert.True(status == 200, error?.Message);
        var row = Assert.Single(response!.Rows);
        Assert.Equal(Id("ord_0007"), row.Id);
        Assert.Equal("REF-0007", Value(row, "ext_name"));
        Assert.Null(response.Next);
    }

    [Fact]
    public async Task List_PrimaryKeyEquality_MissingRecord_EmptyPage()
    {
        var (status, response, _) = await Host.Execute(List("ext_order", 50, Where("ext_orderid", Ops.Eq, Id("ord_9999"))));
        Assert.Equal(200, status);
        Assert.Empty(response!.Rows);
        var (foreign, other, _) = await Host.Execute(List("ext_order", 50, Where("ext_orderid", Ops.Eq, Guid.NewGuid().ToString())));
        Assert.Equal(200, foreign);
        Assert.Empty(other!.Rows);
    }

    [Fact]
    public async Task List_PrimaryKeyWithOtherConditions_StillUnsupported()
    {
        var filter = Where("ext_orderid", Ops.Eq, Id("ord_0007"));
        filter.Conditions.Add(new Condition { Col = "ext_customerref", Op = Ops.Eq, Values = new() { "CUST-07" } });
        var (status, _, error) = await Host.Execute(List("ext_order", 50, filter));
        Assert.Equal(400, status);
        Assert.Contains("ext_orderid", error!.Message);
    }

    [Fact]
    public async Task Retrieve_ByReversibleKey()
    {
        var request = HostFactory.Request("ext_order", Messages.Retrieve);
        request.Id = Id("ord_0005");
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal(request.Id, response!.Record.Id);
        Assert.Equal("REF-0005", Value(response.Record, "ext_name"));
        Assert.Equal("CUST-05", Value(response.Record, "ext_customerref"));
        var total = response.Record.Cells.Single(c => c.Col == "ext_total");
        Assert.Equal((CellTypes.Decimal, "52.5"), (total.Type, total.Value));
    }

    [Fact]
    public async Task Create_ReturnsEncodedKey()
    {
        var request = HostFactory.Request("ext_order", Messages.Create);
        request.Record = new() { Cell("ext_customerref", "CUST-77"), Cell("ext_lines_json", """[{"sku":"A","qty":1}]"""), Cell("ext_total", "12.50", CellTypes.Decimal) };
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal(Id("ord_0121"), response!.Id);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = response.Id;
        var (_, created, _) = await Host.Execute(get);
        Assert.Equal("CUST-77", Value(created!.Record, "ext_customerref"));
        Assert.Equal("12.5", Value(created.Record, "ext_total"));
    }

    [Fact]
    public async Task Create_MissingCustomer_MappedValidation400()
    {
        var request = HostFactory.Request("ext_order", Messages.Create);
        request.Record = new() { Cell("ext_lines_json", "[]") };
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("customer.ref is required", error.Message);
    }

    [Fact]
    public async Task Update_SendsChangedColumnsOnly()
    {
        var update = HostFactory.Request("ext_order", Messages.Update);
        update.Id = Id("ord_0002");
        update.Record = new() { Cell("ext_customerref", "CUST-99") };
        var (status, _, error) = await Host.Execute(update);
        Assert.True(status == 200, error?.Message);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = update.Id;
        var (_, after, _) = await Host.Execute(get);
        Assert.Equal("CUST-99", Value(after!.Record, "ext_customerref"));
        Assert.Equal("REF-0002", Value(after.Record, "ext_name"));
    }

    [Fact]
    public async Task Update_UnboundColumn_400ValidationNamingColumn()
    {
        var update = HostFactory.Request("ext_order", Messages.Update);
        update.Id = Id("ord_0002");
        update.Record = new() { Cell("ext_customerref", "CUST-99"), Cell("ext_placed", "2026-01-01T00:00:00.0000000Z", CellTypes.DateTime) };
        var (status, _, error) = await Host.Execute(update);
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("ext_placed", error.Message);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = update.Id;
        Assert.Equal("CUST-02", Value((await Host.Execute(get)).Response!.Record, "ext_customerref"));
    }

    [Fact]
    public async Task Update_OptionSet_WrittenAsSourceValue()
    {
        var update = HostFactory.Request("ext_order", Messages.Update);
        update.Id = Id("ord_0002");
        update.Record = new() { Cell("ext_status", "100000002", CellTypes.OptionSet) };
        Assert.Equal(200, (await Host.Execute(update)).Status);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = update.Id;
        var record = (await Host.Execute(get)).Response!.Record;
        Assert.Equal("100000002", Value(record, "ext_status"));
        Assert.Equal("""[{"sku":"SKU-02","qty":3}]""", Value(record, "ext_lines_json"));
    }

    [Fact]
    public async Task Create_OptionSetAndLines_ReadBack()
    {
        var create = HostFactory.Request("ext_order", Messages.Create);
        create.Record = new() { Cell("ext_customerref", "CUST-78"), Cell("ext_status", "100000001", CellTypes.OptionSet), Cell("ext_lines_json", """[{"sku":"A","qty":2}]""") };
        var (status, created, _) = await Host.Execute(create);
        Assert.Equal(200, status);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = created!.Id;
        var record = (await Host.Execute(get)).Response!.Record;
        Assert.Equal("100000001", Value(record, "ext_status"));
        Assert.Equal("""[{"sku":"A","qty":2}]""", Value(record, "ext_lines_json"));
    }

    [Fact]
    public async Task Create_UnboundColumn_400_NullUnboundTolerated()
    {
        var create = HostFactory.Request("ext_order", Messages.Create);
        create.Record = new() { Cell("ext_customerref", "CUST-77"), Cell("ext_name", "MY-REF") };
        var (status, _, error) = await Host.Execute(create);
        Assert.Equal(400, status);
        Assert.Contains("ext_name", error!.Message);

        create.Record = new() { Cell("ext_customerref", "CUST-77"), new Cell { Col = "ext_name", Type = CellTypes.String, Value = null } };
        Assert.Equal(200, (await Host.Execute(create)).Status);
    }

    [Fact]
    public async Task Delete_ThenRetrieve404()
    {
        var delete = HostFactory.Request("ext_order", Messages.Delete);
        delete.Id = Id("ord_0003");
        Assert.Equal(200, (await Host.Execute(delete)).Status);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = delete.Id;
        var (status, _, error) = await Host.Execute(get);
        Assert.Equal(404, status);
        Assert.Equal("NotFound", error!.Error);
    }
}
