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
| Ja | ikke sat | – | – | – | Afvist: *Udleveringsdato skal udfyldes ved overførsel til JD Logistik* |
| Ja | sat | tom | – | – | Afvist: *Sporingsnote (xTrackingNote) skal udfyldes ved overførsel til JD Logistik* |
| Ja | sat | udfyldt | tom | – | Afvist: *Transporttype skal vælges ved overførsel til JD Logistik* |
| Ja | sat | udfyldt | JD Logistik Transport | tom | Afvist: *Leveringstype (GLS / Palle Fragt) skal vælges når transporttype er 'JD Logistik Transport'* |
| Ja | sat | udfyldt | JD Logistik Transport | GLS / Palle Fragt | **OK** |
| Ja | sat | udfyldt | Ekstern Transport / Afhenter Selv | udfyldt | Afvist: *Leveringstype skal være tom når transporttype er '…'* |
| Ja | sat | udfyldt | Ekstern Transport / Afhenter Selv | tom | **OK** |
| Ja | sat | udfyldt | *(ukendt værdi)* | vilkårlig | **OK** (pass-through — plugin'et blokerer aldrig på værdier det ikke kender) |

Begrundelse for "leveringstype skal være tom": `SalesOrderMapper` i integrationen lader
`xDeliveryType` overstyre transporttypen — en udfyldt leveringstype ville booke forkert fragt.

Uniconta-klienten viser den returnerede streng direkte til brugeren, så fejlbeskederne er
danske og selvforklarende (ikke kun feltnavne).

## Byg

```bash
dotnet build NeroTrade.UnicontaPlugin/NeroTrade.UnicontaPlugin.csproj -c Release -f net48
```

Output: `NeroTrade.UnicontaPlugin/bin/Release/net48/NeroTrade.UnicontaPlugin.dll`

Upload **kun** `NeroTrade.UnicontaPlugin.dll`. Mappen indeholder også med-kopierede
`Uniconta.*.dll` m.fl. — dem leverer klienten selv; de må ikke uploades.

Projektet multi-targeter `net48;net9.0`: net48 er leverancen (desktopklienten er .NET
Framework), net9.0 er kun den rene valideringskerne så `NeroTrade.UnicontaPlugin.Tests`
kan køre på Linux-CI.

## Tests

```bash
dotnet test NeroTrade.UnicontaPlugin.Tests/NeroTrade.UnicontaPlugin.Tests.csproj
```

Dækker hele matrixen, gaten, whitespace/case-varianter og enum-normalisering
(`GetUserField` kan returnere strengværdien eller et int-indeks — begge håndteres).

## Udrulning

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
- [ ] Gyldig kombination → gem går igennem og integrationen samler ordren op som normalt

## Best-effort: auto-rydning af leveringstype

`Record_PropertyChanged` rydder `xDeliveryType` når transporttypen ændres til
"Ekstern Transport" eller "Afhenter Selv". **Forbehold:** Unicontas PropertyName-semantik
for brugerfelter er ikke dokumenteret — hvis eventet ikke fyrer med feltnavnet, degraderer
funktionen lydløst, og valideringen ved gem er stadig garantien. Handleren er beskyttet mod
event-løkker (re-entrancy-flag + skriver aldrig no-ops) og fanger alle exceptions.

## Fejlsøgning

- **Plugin loader ikke**: tjek at DLL'en er bygget mod en Uniconta.WindowsAPI-version der
  matcher klientens version (pakkeversion = Uniconta-version, pt. 95.0.0.4).
- **Validering blokerer aldrig**: tjek at brugerfelterne findes på DebtorOrder i firmaets
  feltopsætning med præcis de navne der står i `PluginFieldNames.cs` — plugin'et fejler
  bevidst åbent (fail-open) hvis et felt mangler, så brugere aldrig låses ude af gem.
