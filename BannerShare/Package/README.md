# BannerShare
A mod for implementing custom banners.

## Features
Allows custom banners to be put in game by dropping a correctly formatted zip file in your 'my documents' folder.

specifically, after running once, there will be a new folder in my documents called 'Valheim Custom Banners'

you place in there a zip file


Big thanks to Liz for teaching me what a bump map actually does!

## Known issues
will throw errors if your friend builds a banner and you don't have the files.

## Adding a Custom Banner

The zip file you drop into your documents folder needs to have the following files:
### MainTex.png
this is the image file you want on the banner. Resolution is not super important, but the image shape (width:height) should match the banner you base it on (`basePrefab` in Banner.json), otherwise it gets stretched:

| basePrefab | Shape (width:height) | Example size |
|---|---|---|
| piece_banner01 (and the other standard banners) | about 2:5 | 400 x 1000 |
| piece_cloth_hanging_door | 1:2 | 500 x 1000 |
| piece_cloth_hanging_door_blue | about 1:5 | 200 x 1000 |
| piece_cloth_hanging_door_blue2 | about 3:1 | 1500 x 500 |

If the shape is well off, the BepInEx log will warn you and suggest a size.
### BumpMap.png
This is the bump map for the banner. try to keep it to the same resolution as the MainTex.png
### Banner.json
This is the file that tells the mod this is a banner.
this file needs to contain specific this:

```{
  "displayName": "Banner Name",
  "description": "Description of your banner",
  "basePrefab": "piece_banner01",
  "craftingStation": "piece_workbench",
  "Hidden": false,
  "requirements": [
    { "item": "Wood", "amount": 2 },
    { "item": "LeatherScraps", "amount": 4 },
    { "item": "Raspberry", "amount": 2 },
    { "item": "Coal", "amount": 1 }	
  ]
}```