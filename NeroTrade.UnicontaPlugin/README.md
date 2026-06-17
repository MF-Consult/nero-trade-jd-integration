# NeroTrade.UnicontaPlugin

Uniconta Partner Plugin til Neotrade ApS (firma **129192**). Validerer salgsordrer **ved gem**
i Uniconta-desktopklienten, så ordrer markeret til JD-overførsel (`xTransferToJD`) aldrig når
JD Logistik med manglende eller selvmodsigende felter.

Plugin'et er bevidst **uafhængigt** af `NeroTrade.JDIntegration` — det bygges og versioneres
alene. De fire feltnavne er duplikeret i `PluginFieldNames.cs` og **skal** matche
`NeroTrade.JDIntegration/Services/UnicontaHandler/Constants/UnicontaUserFields.cs`
(`PluginFieldNamesTests` fejler hvis de driver fra hinanden).

## Valideringsmatrix

Valideringen kører i `CheckMandatoryFields` (PageEventsBase) ved hvert gem og returnerer ved
første fejl. Alle strengsammenligninger er trimmede og case-insensitive.

| xTransferToJD | Leveringsdato | xTrackingNote | xTransportTypes | xDeliveryType | Resultat |
|---|---|---|---|---|---|
| Nej | – | – | – | – | **OK** (kladde-flow valideres aldrig) |
| Ja | ikke sat | – | – | – | Afvist: *Leveringsdato skal udfyldes ved overførsel til JD Logistik* |
| Ja | sat | tom | – | – | Afvist: *Sporingsnote (xTrackingNote) skal udfyldes ved overførsel til JD Logistik* |
| Ja | sat | udfyldt | tom | – | Afvist: *Transporttype skal vælges ved overførsel til JD Logistik* |
| Ja | sat | udfyldt | JD Logistik Transport | tom | Afvist: *Leveringstype (GLS / Palle Fragt) skal vælges når transporttype er 'JD Logistik Transport'* |
| Ja | sat | udfyldt | JD Logistik Transport | GLS / Palle Fragt | **OK** |
| Ja | sat | udfyldt | Ekstern Transport / Afhenter Selv | udfyldt | Afvist: *Leveringstype skal være tom når transporttype er '…'* |
| Ja | sat | udfyldt | Ekstern Transport / Afhenter Selv | tom | **OK** |
| Ja | sat | udfyldt | *(ukendt værdi)* | vilkårlig | **OK** (pass-through — plugin'et blokerer aldrig på værdier det ikke kender) |

**Byttepaller (`xByttepaller`) — tvungen stillingtagen, kun for palleordrer:** `xByttepaller`
(værdiliste Ja/Nej) **skal** være valgt — men **kun** når ordren er en palleordre, dvs. præcis de
tilfælde hvor integrationen overhovedet kan sende `PL_EXCHANGE`: `Leveringstype = Palle Fragt`,
**eller** tom leveringstype + `Transporttype = Ekstern Transport` (samme detektion som
`SalesOrderMapper`). For GLS/parcel og Afhenter Selv er feltet irrelevant — det **skjules** og
kræves ikke. På en palleordre med tom byttepaller afvises gem: *Byttepaller (Ja/Nej) skal vælges ved
overførsel til JD Logistik* (tjekket kører sidst, efter transport-/leveringstype). Selve værdien
gater `PL_EXCHANGE`: kun **Ja** sender den til JD (Maiwand bad om manuel styring pr. ordre; tidligere
blev `PL_EXCHANGE` sendt ubetinget på alle paller). Feltet vises/skjules som `xDeliveryType` (via
`ApplyFieldVisibility` ved page-load og ved transport-/leveringstype-ændringer).

Begrundelse for "leveringstype skal være tom": `SalesOrderMapper` i integrationen lader
`xDeliveryType` overstyre transporttypen — en udfyldt leveringstype ville booke forkert fragt.

Uniconta-klienten viser den returnerede streng direkte til brugeren, så fejlbeskederne er
danske og selvforklarende (ikke kun feltnavne).

## Byg

```bash
dotnet build NeroTrade.UnicontaPlugin/NeroTrade.UnicontaPlugin.csproj -c Release -f net4.8
```

Output: `NeroTrade.UnicontaPlugin/bin/Release/net4.8/NeroTrade.UnicontaPlugin.dll`

Upload **kun** `NeroTrade.UnicontaPlugin.dll`. Mappen indeholder også med-kopierede
`Uniconta.*.dll` m.fl. — dem leverer klienten selv; de må ikke uploades.

Projektet multi-targeter `net4.8;net9.0`: net4.8 er leverancen (desktopklienten er .NET
Framework), net9.0 er kun den rene valideringskerne (ingen Uniconta-typer) så
`NeroTrade.UnicontaPlugin.Tests` kan køre på Linux-CI.

I **Debug** kopierer build'et automatisk DLL + .pdb til `C:\Uniconta\PluginPath`
(kun net4.8 + Debug + Windows). Kører Uniconta-klienten, er DLL'en låst — så ses en
copy-**advarsel** (ikke en build-fejl); luk klienten og byg igen for at loade den nye version.

## Tests

```bash
dotnet test NeroTrade.UnicontaPlugin.Tests/NeroTrade.UnicontaPlugin.Tests.csproj
```

Dækker hele matrixen, gaten, whitespace/case-varianter og enum-normalisering
(`GetUserField` kan returnere strengværdien eller et int-indeks — begge håndteres).

## Udrulning

0. **Opret brugerfeltet** (engangsopsætning, firma **129192**): på DebtorOrder oprettes
   `xByttepaller` som **værdiliste** med værdierne `Ja` og `Nej`, **ingen** default (tom). Notér
   indeks-rækkefølgen og afstem `ExchangePalletsValues.InIndexOrder` hvis den afviger fra `{ Ja, Nej }`.
   Den **fulde** liste over salgs- og indkøbsordrefelter (navn, format, værdilister) står i
   [`docs/operations.md` §4.4](../docs/operations.md#44-uniconta-user-fields-sales--purchase-orders).
1. **Upload DLL'en**: Administration → **Partner Plugin** → Add.
   - Name: fx `NeroTrade JD Validering`
   - Select file: `NeroTrade.UnicontaPlugin.dll`
   - Company ID: **129192**
   - DLL-1/DLL-2 giver plads til to samtidige versioner ved Uniconta-opgraderinger.
2. **Bind til salgsordresiden**: Tools → **User Plugin** → tilføj række:
   - Control: `DebtorOrders` (verificér det præcise kontrolnavn med **F12** på salgsordresiden)
   - Name of Dll: `NeroTrade.UnicontaPlugin`
   - ClassName: `SalesOrderJdValidationPlugin`
   - Type: Event
3. Genstart Uniconta-klienten og test matrixen manuelt (se tjekliste nedenfor).

### Manuel accepttest

- [ ] `xTransferToJD` = Nej: ordre kan gemmes uanset tomme felter
- [ ] `xTransferToJD` = Ja uden leveringsdato → gem afvises med dansk besked
- [ ] ... uden sporingsnote → afvises
- [ ] ... uden transporttype → afvises
- [ ] JD Logistik Transport uden leveringstype → afvises
- [ ] Ekstern Transport / Afhenter Selv **med** leveringstype → afvises
- [ ] Palleordre (Palle Fragt / Ekstern Transport): `xByttepaller` vist; tom → gem afvises; Ja/Nej → går igennem
- [ ] Ikke-palleordre (GLS / Afhenter Selv): `xByttepaller` skjult og ikke krævet (gem går igennem uanset)
- [ ] Gyldig kombination → gem går igennem og integrationen samler ordren op som normalt
- [ ] Vælg JD Logistik Transport → `xDeliveryType` udfyldes auto med **Palle Fragt**, feltet **vises**, GLS kan stadig vælges
- [ ] Skift til Ekstern Transport / Afhenter Selv → `xDeliveryType` ryddes og feltet **skjules**
- [ ] Åbn en ordre der allerede er sat til ≠ JD Logistik → feltet er skjult fra start (`OnPageLayoutLoaded`)

## Best-effort: leveringstype styret af transporttype

`Record_PropertyChanged` reagerer når transporttypen ændres (reglerne ligger i den rene,
testdækkede `DeliveryTypeRules`):

| Ny transporttype | `xDeliveryType` | Feltet i UI'en |
|---|---|---|
| JD Logistik Transport | sættes til **Palle Fragt** hvis tomt (en eksisterende værdi bevares, GLS kan stadig vælges) | **vises** |
| Ekstern Transport / Afhenter Selv | **ryddes** | **skjules** |
| *(ukendt værdi)* | røres ikke | skjules |

Skjul/vis sker via `PageEventsBase.GetFormControl(...)` på kontrollen i
`PluginControlNames.DeliveryType`. Den initiale tilstand sættes i `OnPageLayoutLoaded` (når en
eksisterende ordre åbnes) og dynamisk i `Record_PropertyChanged` (når transporttypen ændres).
Efter et auto-sat/ryddet felt kaldes `NotifyPropertyChanged` så værdien vises med det samme uden
refresh. **Bemærk:** på en *helt ny, blank* ordre vises feltet indtil en transporttype vælges —
Uniconta rendrer kontrollen efter vores hooks, så den case kan ikke styres herfra (valideringen
ved gem er uændret garantien).

**Forbehold (alt herover er best-effort, ikke garanti):**
- Unicontas PropertyName-semantik for brugerfelter er ikke dokumenteret — fyrer eventet ikke
  med feltnavnet, degraderer default/ryd lydløst, og **valideringen ved gem er garantien**.
- Kontrolnavnet for skjul er ikke nødvendigvis lig feltnavnet — **verificér med F12** på
  salgsordresiden og ret `PluginControlNames.DeliveryType` hvis feltet ikke skjules.
- Handleren er beskyttet mod event-løkker (re-entrancy-flag + skriver aldrig no-ops),
  bruger ingen WPF-build-afhængighed (sætter `Visibility` via reflection, så net4.8 stadig
  kan kompileres på Linux-CI), og fanger alle exceptions.

## Fejlsøgning

- **Plugin loader ikke**: tjek at DLL'en er bygget mod en Uniconta.WindowsAPI-version der
  matcher klientens version (pakkeversion = Uniconta-version, pt. 95.0.0.4).
- **Validering blokerer aldrig**: tjek at brugerfelterne findes på DebtorOrder i firmaets
  feltopsætning med præcis de navne der står i `PluginFieldNames.cs` — plugin'et fejler
  bevidst åbent (fail-open) hvis et felt mangler, så brugere aldrig låses ude af gem.
