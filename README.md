# Starwars Fandom Wiki infobox data

The contents of this repo is the extracted infobox data from the star wars fandom wiki in json format.

# Relationships

This property is *not* from the infobox data. It's an additional property that provides all other infoboxes that have linked to this infobox.

For this reason, I have structured it differently from the standard format of `Values` and `Links` used for all other object properties.

```json
"_Relationships_": {
  "Mentions": [
    {
      "PageId": 1,
      "PageTitle": "PageTitle",
      "PageUrl": "https://starwars.fandom.com/wiki/PageTitle",
      "Template": "Template"
    }
  ]
}
```

As an example, say you would like to find as much as possible linked to the planet `Naboo`, you would find all other infoboxes that mention Naboo in the bottom of the `Planet - Naboo.json` file, which you can see contains everything by Template such as `Family`, `City`, `Structure`, `Battle` etc.

```json
...

{
    "PageId": 639600,
    "PageTitle": "Palpatine family",
    "PageUrl": "https://starwars.fandom.com/wiki/Palpatine_family",
    "Template": "Family"
},
{
    "PageId": 476340,
    "PageTitle": "Emerald wine",
    "PageUrl": "https://starwars.fandom.com/wiki/Emerald_wine",
    "Template": "Food"
},
{
    "PageId": 712926,
    "PageTitle": "Qui-Gon Jinn shrine",
    "PageUrl": "https://starwars.fandom.com/wiki/Qui-Gon_Jinn_shrine",
    "Template": "Structure"
},
{
    "PageId": 647010,
    "PageTitle": "Gungan-Naboo dispute",
    "PageUrl": "https://starwars.fandom.com/wiki/Gungan-Naboo_dispute",
    "Template": "Battle"
},
{
    "PageId": 452857,
    "PageTitle": "Theed",
    "PageUrl": "https://starwars.fandom.com/wiki/Theed",
    "Template": "City"
}

...
```





