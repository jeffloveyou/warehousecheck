using Warehouse.Domain;
using Warehouse.Domain.Items;

namespace Warehouse.UnitTests.Domain;

/// <summary>M1-02 物料狀態流程（T-M1-02-02～04）：合法轉換、非法轉換、核准後的修改限制、審核紀錄規則。</summary>
public class ItemWorkflowTests
{
    private static readonly Guid CategoryId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private static Item NewItem() =>
        Item.Submit(Guid.NewGuid(), "MR-000001", "伺服放大器", CategoryId, UnitId, TrackingMode.None,
            "三菱", "MR-J4-10A", null, Now, Guid.NewGuid());

    private static Item InStatus(ItemStatus status)
    {
        var item = NewItem();
        switch (status)
        {
            case ItemStatus.Returned:
                item.Return(Now);
                break;
            case ItemStatus.Rejected:
                item.Reject(Now);
                break;
            case ItemStatus.Active:
                item.Approve("EL-000001", Now);
                break;
            case ItemStatus.Inactive:
                item.Approve("EL-000001", Now);
                item.Deactivate(Now);
                break;
        }
        return item;
    }

    [Fact]
    public void 提出時計算比對欄位並記錄提出人()
    {
        var item = NewItem();

        Assert.Equal("伺服放大器", item.NormalizedName);
        Assert.Equal("MRJ410A", item.NormalizedPartNo);
        Assert.NotNull(item.CreatedById);
    }

    [Fact]
    public void 核准_發出料號並變為有效()
    {
        var item = NewItem();

        item.Approve("EL-000125", Now);

        Assert.Equal(ItemStatus.Active, item.Status);
        Assert.Equal("EL-000125", item.ItemCode);
    }

    [Theory]
    [InlineData("EL-125")]
    [InlineData("el-000125")]
    [InlineData("")]
    public void 核准_料號格式錯誤被拒且狀態不變(string code)
    {
        var item = NewItem();

        Assert.Throws<DomainRuleException>(() => item.Approve(code, Now));
        Assert.Equal(ItemStatus.PendingApproval, item.Status);
        Assert.Null(item.ItemCode);
    }

    // T-D32-10
    [Fact]
    public void 變更料號_只限已核准_格式正確_不可與目前相同()
    {
        var item = NewItem();
        Assert.Throws<DomainRuleException>(() => item.ChangeItemCode("EL-000200", Now));

        item.Approve("EL-000125", Now);
        Assert.Throws<DomainRuleException>(() => item.ChangeItemCode("EL-200", Now));
        Assert.Throws<DomainRuleException>(() => item.ChangeItemCode("EL-000125", Now));
        Assert.Throws<DomainRuleException>(() => item.ChangeItemCode("EL-00000000000001", Now)); // 17 字，超過上限 16

        item.ChangeItemCode("ELA-000001", Now);
        Assert.Equal("ELA-000001", item.ItemCode);
        Assert.Equal(ItemStatus.Active, item.Status);
    }

    [Fact]
    public void 料號前綴與格式判斷()
    {
        Assert.Equal("ELA", Item.PrefixOf("ELA-000001"));
        Assert.True(Item.IsValidItemCode("ME-000123"));
        Assert.False(Item.IsValidItemCode("me-000123"));
        Assert.False(Item.IsValidItemCode(null));
    }

    [Fact]
    public void 退回後修改重送_回到待核准且不占料號()
    {
        var item = NewItem();
        item.Return(Now);

        item.UpdateDetails("伺服放大器 200W", CategoryId, UnitId, TrackingMode.Serial, null, "MR-J4-20A", null, Now);
        item.Resubmit(Now);

        Assert.Equal(ItemStatus.PendingApproval, item.Status);
        Assert.Null(item.ItemCode);
        Assert.Equal(TrackingMode.Serial, item.TrackingMode);
        Assert.Equal("MRJ420A", item.NormalizedPartNo);
    }

    [Theory]
    [InlineData(ItemStatus.PendingApproval)]
    [InlineData(ItemStatus.Returned)]
    public void 撤回_待核准或退回可撤回(ItemStatus status)
    {
        var item = InStatus(status);

        item.Withdraw(Now);

        Assert.Equal(ItemStatus.Rejected, item.Status);
    }

    [Fact]
    public void 停用與重新啟用_料號保留()
    {
        var item = InStatus(ItemStatus.Active);

        item.Deactivate(Now);
        Assert.Equal(ItemStatus.Inactive, item.Status);
        item.Reactivate(Now);

        Assert.Equal(ItemStatus.Active, item.Status);
        Assert.Equal("EL-000001", item.ItemCode);
    }

    // 非法轉換：每個動作只接受特定的起始狀態
    public static TheoryData<ItemStatus, string> IllegalTransitions => new()
    {
        { ItemStatus.Returned, "Approve" }, { ItemStatus.Rejected, "Approve" }, { ItemStatus.Active, "Approve" },
        { ItemStatus.Returned, "Return" }, { ItemStatus.Active, "Return" },
        { ItemStatus.Rejected, "Reject" }, { ItemStatus.Active, "Reject" },
        { ItemStatus.PendingApproval, "Resubmit" }, { ItemStatus.Active, "Resubmit" },
        { ItemStatus.Rejected, "Withdraw" }, { ItemStatus.Active, "Withdraw" }, { ItemStatus.Inactive, "Withdraw" },
        { ItemStatus.PendingApproval, "Deactivate" }, { ItemStatus.Inactive, "Deactivate" },
        { ItemStatus.PendingApproval, "Reactivate" }, { ItemStatus.Active, "Reactivate" },
    };

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void 非法狀態轉換_被拒且狀態不變(ItemStatus status, string action)
    {
        var item = InStatus(status);
        var codeBefore = item.ItemCode;

        Action act = action switch
        {
            "Approve" => () => item.Approve("EL-000099", Now),
            "Return" => () => item.Return(Now),
            "Reject" => () => item.Reject(Now),
            "Resubmit" => () => item.Resubmit(Now),
            "Withdraw" => () => item.Withdraw(Now),
            "Deactivate" => () => item.Deactivate(Now),
            "Reactivate" => () => item.Reactivate(Now),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        Assert.Throws<DomainRuleException>(act);
        Assert.Equal(status, item.Status);
        Assert.Equal(codeBefore, item.ItemCode);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("unit")]
    [InlineData("tracking")]
    public void 核准後_不可改分類_基本單位_追蹤模式(string field)
    {
        var item = InStatus(ItemStatus.Active);

        var ex = Assert.Throws<DomainRuleException>(() => item.UpdateDetails(
            "伺服放大器",
            field == "category" ? Guid.NewGuid() : CategoryId,
            field == "unit" ? Guid.NewGuid() : UnitId,
            field == "tracking" ? TrackingMode.Lot : TrackingMode.None,
            null, null, null, Now));
        Assert.Contains("核准後", ex.Message);
    }

    [Fact]
    public void 核准後_仍可改品名與描述欄位()
    {
        var item = InStatus(ItemStatus.Inactive);

        item.UpdateDetails("伺服放大器（停產）", CategoryId, UnitId, TrackingMode.None, "三菱", null, "已停產", Now);

        Assert.Equal("伺服放大器（停產）", item.Name);
        Assert.Null(item.NormalizedPartNo);
    }

    [Fact]
    public void 已拒絕_不可修改()
    {
        var item = InStatus(ItemStatus.Rejected);

        Assert.Throws<DomainRuleException>(() =>
            item.UpdateDetails("x", CategoryId, UnitId, TrackingMode.None, null, null, null, Now));
    }

    [Fact]
    public void 允許負庫存時_不可改為序號追蹤()
    {
        var item = NewItem();
        item.SetNegativeStockPolicy(true, 5, Now);

        Assert.Throws<DomainRuleException>(() =>
            item.UpdateDetails("伺服放大器", CategoryId, UnitId, TrackingMode.Serial, null, null, null, Now));
    }

    [Theory]
    [InlineData(ItemReviewAction.Return)]
    [InlineData(ItemReviewAction.Reject)]
    public void 審核紀錄_退回與拒絕必須有意見(ItemReviewAction action)
    {
        Assert.Throws<DomainRuleException>(() => ItemReview.Record(
            Guid.NewGuid(), action, ItemStatus.PendingApproval, ItemStatus.Returned, Guid.NewGuid(), "王小明", Now, "  "));
    }

    [Fact]
    public void 審核紀錄_只有拒絕可註明重複_且不能重複於自己()
    {
        var itemId = Guid.NewGuid();

        Assert.Throws<DomainRuleException>(() => ItemReview.Record(
            itemId, ItemReviewAction.Return, ItemStatus.PendingApproval, ItemStatus.Returned, Guid.NewGuid(), "王小明", Now,
            "請補型號", duplicateOfItemId: Guid.NewGuid()));
        Assert.Throws<DomainRuleException>(() => ItemReview.Record(
            itemId, ItemReviewAction.Reject, ItemStatus.PendingApproval, ItemStatus.Rejected, Guid.NewGuid(), "王小明", Now,
            "重複", duplicateOfItemId: itemId));
    }
}
