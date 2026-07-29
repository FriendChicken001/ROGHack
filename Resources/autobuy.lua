-- AutoBuyItem framework
-- Call AutoBuyItem(item_id, minCount, buyQty) to start watching an item:
-- whenever your bag count of item_id drops to or below minCount, it will
-- automatically buy buyQty more from whichever shop sells it.
-- Call AutoBuyItem(item_id, 0, 0) to stop watching an item.

local function _abLog(...)
   local f = io.open("ROGHack_debug.txt", "a")
   local p = {}
   for i = 1, select("#", ...) do p[i] = tostring(select(i, ...)) end
   f:write(table.concat(p, " ") .. "\n")
   f:close()
end

local function ReqBuyShopItem(table_id, Qty, shop_id)
   if(Qty > 0)then
      MgrMgr:GetMgr("ShopMgr").RequestBuyShopItem(table_id, Qty, false, false, shop_id)
      return true
   end
   return false
end

local function BuyShopItemByItemId(item_id, Qty)
   local l_buyQty = tonumber(Qty) or 0
   if(l_buyQty == 0)then return end
   local shops = TableUtil.GetShopCommoditTable().GetTable()
   local found = false
   for _, commodity in pairs(shops) do
      if commodity.ItemId == item_id then
         found = true
         _abLog("BuyShopItemByItemId: found commodity", commodity.Id, "shop", commodity.ShopId, "for item", item_id)
         ReqBuyShopItem(commodity.Id, Qty, commodity.ShopId)
         break
      end
   end
   if not found then
      _abLog("BuyShopItemByItemId: NO shop commodity sells item", item_id)
   end
end

-- Plain "AutoBuyItem = ..." global assignment is silently discarded by this game's
-- Lua sandbox (writes to bare globals don't stick, even within the same chunk).
-- rawset() bypasses whatever __newindex metamethod is intercepting it.
local AutoBuyItemImpl = (function()
   local AutoBuyList = {}
   local notShowItemNotice = {}

   local CheckToBuyItem = function(item_id)
      local toBuy = AutoBuyList[item_id]
      if(toBuy ~= nil)then
         local count = Data.BagModel:GetCoinOrPropNumById(item_id)
         local l_minCount = toBuy.minCount
         local l_buyQty = toBuy.buyQty
         local buyQty = (l_minCount - count) + l_buyQty
         _abLog("CheckToBuyItem: item", item_id, "count=", count, "minCount=", l_minCount, "buyQty=", buyQty)
         if(count <= l_minCount)then
            notShowItemNotice[item_id] = buyQty
            BuyShopItemByItemId(item_id, buyQty)
         end
      end
   end

   local started = false
   local startHooks = function()
       if(started == true)then return end
       started = true

       -- suppress the "item received" toast for our own auto-buys
       _hook.clear(MgrMgr:GetMgr("NoticeMgr").NoticeNormalTips)
       MgrMgr:GetMgr("NoticeMgr").NoticeNormalTips = _hook.add(MgrMgr:GetMgr("NoticeMgr").NoticeNormalTips, function(args)
          local item_id = args[1]
          local itemCount = args[3]
          local notShow = notShowItemNotice[item_id]
          if(notShow ~= nil and notShow > 0)then
             notShow = notShow - itemCount
             notShowItemNotice[item_id] = notShow
             return true
          end
       end)

       local l_GameEventMgr = MgrMgr:GetMgr("GameEventMgr")
       EventDispatcherAdd(l_GameEventMgr.l_eventDispatcher, l_GameEventMgr.OnBagUpdate, function(itemUpdateDataList)
          for i = 1, #itemUpdateDataList do
             local singleUpdateData = itemUpdateDataList[i]
             local itemdata = singleUpdateData:GetNewOrOldItem()
             if(itemdata ~= nil)then
                CheckToBuyItem(itemdata.ItemConfig.ItemID)
             end
          end
          return true
       end).init()
   end

   return function(item_id, minCount, buyQty)
      startHooks()
      AutoBuyList[item_id] = nil
      local l_minCount = tonumber(minCount) or 0
      local l_buyQty = tonumber(buyQty) or 0
      if(l_buyQty == 0)then return end

      local l_itemData = TableUtil.GetItemTable().GetRowByItemID(item_id)
      _abLog("AutoBuyItem: item", item_id, "GetRowByItemID found=", l_itemData ~= nil)
      if(l_itemData ~= nil)then
         AutoBuyList[item_id] = {
            minCount = l_minCount,
            buyQty = l_buyQty,
         }
         CheckToBuyItem(item_id)
      end
   end
end)()

rawset(_G, "AutoBuyItem", AutoBuyItemImpl)

io.write("AutoBuyItem framework loaded. Call AutoBuyItem(item_id, minCount, buyQty) to configure.\n")
