-- Dumps every row of the item table to ROGHack_items.txt (in the game's
-- working directory, e.g. D:\JoyMaker\JoyMakerGame\ROGHack_items.txt).
-- Run once via the "Dump Item Table" GUI button, then search the output
-- file for the item name you're looking for to find its numeric ID.

local items = TableUtil.GetItemTable().GetTable()
local f = io.open("ROGHack_items.txt", "w")

-- Row objects aren't flat Lua tables; some fields (e.g. "_ri") are
-- themselves tables wrapping the real data one level deeper, so we
-- flatten a couple of levels rather than assuming a fixed shape.
local function collectFields(obj, prefix, depth, parts)
   if depth > 2 then return end
   local ok = pcall(function()
      for k, v in pairs(obj) do
         local key = prefix .. tostring(k)
         if type(v) == "table" then
            collectFields(v, key .. ".", depth + 1, parts)
         else
            table.insert(parts, key .. "=" .. tostring(v))
         end
      end
   end)
   if not ok then
      table.insert(parts, prefix .. "?=<unreadable>")
   end
end

local count = 0
for id, row in pairs(items) do
   local parts = {}
   collectFields(row, "", 0, parts)
   if #parts == 0 then
      local id_ok, id_val = pcall(function() return row.ItemID end)
      local name_ok, name_val = pcall(function() return row.Name end)
      table.insert(parts, "ItemID=" .. tostring(id_ok and id_val or "?"))
      table.insert(parts, "Name=" .. tostring(name_ok and name_val or "?"))
   end
   f:write(tostring(id) .. "\t" .. table.concat(parts, "\t") .. "\n")
   count = count + 1
end

f:close()
io.write("Dumped " .. count .. " items to ROGHack_items.txt\n")
