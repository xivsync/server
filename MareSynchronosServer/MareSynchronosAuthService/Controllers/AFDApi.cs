using System.Text.Json.Serialization;

namespace MareSynchronosAuthService.Controllers;

public class AFDApi
{
       public class ApiResponse
    {
        [JsonPropertyName("ec")]
        public int Ec { get; set; } //Error Code

        [JsonPropertyName("em")]
        public string Em { get; set; } //Error message

        [JsonPropertyName("data")]
        public ApiData Data { get; set; }
    }

    public class ApiData
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("order")]
        public Order Order { get; set; }
    }

    public class Order
    {
        [JsonPropertyName("out_trade_no")]
        public string OutTradeNo { get; set; } //订单号

        [JsonPropertyName("custom_order_id")]
        public string CustomOrderId { get; set; } //自定义信息

        [JsonPropertyName("user_id")]
        public string UserId { get; set; } //下单用户ID

        [JsonPropertyName("user_private_id")]
        public string UserPrivateId { get; set; } //UUID

        [JsonPropertyName("plan_id")]
        public string PlanId { get; set; } //方案ID，如自选，则为空

        [JsonPropertyName("month")]
        public int Month { get; set; } //赞助月份

        [JsonPropertyName("total_amount")]
        public string TotalAmount { get; set; }

        [JsonPropertyName("show_amount")]
        public string ShowAmount { get; set; }

        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("remark")]
        public string Remark { get; set; } //订单留言

        [JsonPropertyName("redeem_id")]
        public string RedeemId { get; set; }

        [JsonPropertyName("product_type")]
        public int ProductType { get; set; }

        [JsonPropertyName("discount")]
        public string Discount { get; set; }

        [JsonPropertyName("sku_detail")]
        public List<SkuDetail> SkuDetail { get; set; }

        [JsonPropertyName("address_person")]
        public string AddressPerson { get; set; }

        [JsonPropertyName("address_phone")]
        public string AddressPhone { get; set; }

        [JsonPropertyName("address_address")]
        public string AddressAddress { get; set; }
    }

    public class SkuDetail
    {
        [JsonPropertyName("sku_id")]
        public string SkuId { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("album_id")]
        public string AlbumId { get; set; }

        [JsonPropertyName("pic")]
        public string Pic { get; set; }
    }
}