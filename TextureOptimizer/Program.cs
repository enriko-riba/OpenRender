using OpenRender.Core.Textures;

var list = new List<MaterialData>
{
    new MaterialData("a", new int[] {3, 1, 20} ),
    new MaterialData("b", new int[] {11, 9, 23, 22, 27, 0, 16}),
    new MaterialData("c", new int[] {23, 12, 25, 15, 0, 22, 5, 7}),
    new MaterialData("d", new int[] {0, 19, 16}),
    new MaterialData("e", new int[] {1, 0}),
    new MaterialData("f", new int[] {3, 22, 14, 11, 25, 20, 0}),
    new MaterialData("g", new int[] {1, 0, 8, 4}),
    new MaterialData("h", new int[] {0, 1, 26}),
    new MaterialData("i", new int[] {10, 2, 0, 4}),
    new MaterialData("j", new int[] {0, 1, 4}),
    new MaterialData("k", new int[] {0, 2, 5}),
    new MaterialData("l", new int[] {0, 19, 1}),
    new MaterialData("m", new int[] {9, 16, 23, 8, 0, 14, 20}),
    new MaterialData("n", new int[] {1, 2, 21 ,4}),
    new MaterialData("o", new int[] {22 ,5 ,7 ,2 ,25 ,24 ,23 ,15}),
    new MaterialData("p", new int[] {23 ,11 ,25 ,15}),
    new MaterialData("q", new int[] {6 ,7 ,8 ,9}),
    new MaterialData("r", new int[] {16 ,18 ,19 ,17}),
    new MaterialData("s", new int[] {1 ,22 ,17 ,18 ,25 ,13 ,0 ,15})
};

var TextureUnitsCount = 20;

var batcher = new TextureBatcher(TextureUnitsCount);
batcher.SortMaterials(list);
foreach (var material in list) batcher.AssignBatch(material);
foreach (var material in list) batcher.AssignBatch(material);
foreach (var material in list) batcher.AssignBatch(material);

//  dump results
Console.OutputEncoding = System.Text.Encoding.Unicode;
Console.Clear();
TableHelper.DisplayTableHeader(TextureUnitsCount);
TextureUnitUsage[]? previous = null;
var i = 0;
foreach (var unitsUsages in batcher.UnitsHistory)
{
    TableHelper.DisplayTextureUnits(TextureUnitsCount, list, i++, unitsUsages, previous);
    previous = unitsUsages;
}