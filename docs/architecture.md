# MonitorWay: architektura (plan)

Stan: M0 zrobione 2026-10-21, M1 (ślady), M2 (błędy), M3 (metryki), M4 (MCP) i M5 (OTLP) 2026-10-25.
M6 (Logle) odłożone. Pierwsze wydanie: `2.0.0-preview`.
Decyzje 2026-10-21: SDK OpenTelemetry, OTLP w M5 przed stabilnym wydaniem, w v1 tylko pamięć, wersja `2.0.0-preview`.

## 1. Cel

Jedna osadzana biblioteka dla ASP.NET Core, która zbiera i pokazuje wszystkie sygnały OpenTelemetry:
logi, ślady (traces), metryki i błędy. Działa wewnątrz aplikacji, także na produkcji, bez kolektora
i bez osobnego serwera. Zastępuje LogWay.

Zasada: zgodność z OpenTelemetry w obie strony.
- Do zbierania danych używamy standardowego SDK OpenTelemetry .NET. MonitorWay jest dla niego kolejnym
  eksporterem, obok np. OTLP do Grafany czy Jaegera. Instrumentacje (ASP.NET Core, HttpClient, EF Core,
  runtime) są te same, których i tak używa się z OTel.
- Model danych odpowiada modelowi OTel (zasób, zakres instrumentacji, atrybuty, konwencje semantyczne).
  Dzięki temu w późniejszej fazie MonitorWay może też przyjmować dane przez OTLP od innych usług, w dowolnym
  języku.

Wyróżnik wobec dashboardu Aspire, Jaegera czy Grafany: nic nie trzeba stawiać obok aplikacji. Wyróżnik
wobec LogWay: wszystkie sygnały i powiązania między nimi (log -> ślad -> błąd -> metryka).

## 2. Punkt wyjścia: LogWay i Logle

- LogWay nie był wydany (brak gita i paczek na NuGet), więc nie ma użytkowników do migracji. Kod
  przenosimy do nowego repo `kododo-dev/MonitorWay`, a LogWay zostaje porzucony.
- Do przejęcia z LogWay: bufor z pulami retencji (`MemoryLogBuffer`, `RetentionPool`), czytnik plików
  z indeksem chunków (format Docker, CLEF, JSON, tekst; 10 GB, pierwsza strona w 0,03 s), składnia
  wyszukiwania (`SearchExpression`, `LogQueryMatcher`), stronicowanie kursorem, live tail przez SSE, narzędzia
  MCP na Reiho, z SPA: wirtualizowana lista, minimapa gęstości, wybór zakresu czasu, szczegóły wpisu.
- Do zmiany: LogWay nie używa OTel ani Way.Shell, wyjątek trzyma jako tekst, nie ma zasobu (usługi),
  a abstrakcja źródeł (`ILogSource`) jest wewnętrzna.
- Logle używa z LogWay tylko czytnika plików i UI. Musi więc zostać paczka z plikami (sekcja 9).

## 3. Zbieranie danych

### 3.1 W aplikacji: eksportery SDK OpenTelemetry (podstawowa droga)

```csharp
builder.AddMonitorWay(x => x.AddUi());

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMonitorWay())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddMonitorWay())
    .WithLogging(l => l.AddMonitorWay());

app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
```

- Ślady: procesor lub eksporter `BaseExporter<Activity>` (tylko zakończone, próbkowane spany).
- Logi: `BaseExporter<LogRecord>`. Rekord trzeba skopiować w `Export` (SDK używa puli obiektów).
  Zakresy (`IncludeScopes`) i szablon wiadomości jak w LogWay.
- Metryki: `PeriodicExportingMetricReader` z własnym eksporterem `BaseExporter<Metric>`, temporalność
  delta, interwał np. 10 s. Exemplary (powiązanie punktu metryki ze śladem) gdy są włączone.
- Eksport we własnym `SuppressInstrumentationScope`, żeby MonitorWay nie zbierał danych o sobie.
- Opcjonalny skrót `x.UseOpenTelemetryDefaults()`: konfiguruje typowe instrumentacje za użytkownika, dla
  tych, którzy nie używają jeszcze OTel. Pełna kontrola zostaje przy ręcznej konfiguracji powyżej.
- Wersja SDK: OpenTelemetry 1.20.x (net8/9/10).

### 3.2 Z innych usług: odbiornik OTLP (późniejsza faza)

`POST {path}/otlp/v1/traces|metrics|logs`, OTLP/HTTP w protobuf i JSON (bez gRPC w pierwszej wersji).
Dowolna usługa z SDK OTel albo kolektor wysyła dane do MonitorWay. Ten sam model i te same magazyny co
w 3.1, z rozróżnieniem usług po `service.name`. To robi z Logle pełny host observability (sekcja 9).
Typy protobuf generujemy z `opentelemetry-proto` (Google.Protobuf), bo SDK .NET ich nie udostępnia.

### 3.3 Serilog i NLog

LogWay miał własny sink i target. W MonitorWay: Serilog i NLog mają już ścieżki do OTel (np.
`Serilog.Sinks.OpenTelemetry` przez OTLP, albo zapis przez `ILogger`). Decyzja w M0: zostawić cienkie
adaptery czy polegać na OTLP i `ILogger`. Rekomendacja: nie przenosić adapterów na start.

## 4. Model danych (Kododo.MonitorWay.Core, publiczny)

Wspólne: `Resource` (atrybuty, w tym `service.name`, `service.version`, `deployment.environment`),
`Scope` (nazwa i wersja instrumentacji), atrybuty jako typowane wartości (string, bool, long, double,
tablice), a nie wszystko jako tekst.

| Sygnał | Rekord |
|---|---|
| Log | czas, czas zaobserwowania, severity (liczba i tekst), treść, szablon, atrybuty, trace id, span id, nazwa zdarzenia, wyjątek (typ, wiadomość, stos jako atrybuty `exception.*`) |
| Span | trace id, span id, rodzic, nazwa, rodzaj (server, client, internal, producer, consumer), start, koniec, status (unset, ok, error + opis), atrybuty, zdarzenia (w tym `exception`), linki |
| Metryka | nazwa, opis, jednostka, typ (counter, up-down counter, gauge, histogram), punkty: czas, atrybuty, wartość lub histogram (granice, liczności, suma, min, max), exemplary |
| Błąd | grupa (fingerprint), typ, wiadomość, stos, wystąpienia z czasem, usługą, trace id i źródłem (log albo span) |

Abstrakcje magazynów i źródeł są publiczne w Core (w LogWay były wewnętrzne), żeby Logle i inne
hosty mogły dodawać własne źródła.

## 5. Magazyny (w pamięci, z pulami retencji)

Wspólny budżet pamięci (np. 128 MB) z podziałem na sygnały, konfigurowany jak w LogWay ("64MB", wiek,
liczba). Każdy sygnał ma pule, a przy przekroczeniu budżetu najpierw tracimy najmniej cenne dane.

- Logi: bez zmian względem LogWay (pule po poziomach, segmentowana kolejka, globalny id, k-way merge).
- Ślady: przechowujemy całe ślady, nie pojedyncze spany. Retencja po zakończeniu śladu (tail-based):
  osobne pule na ślady z błędem, wolne (próg czasu, np. p95 albo stała) i pozostałe. Dzięki temu po godzinie
  zostają głównie ślady warte obejrzenia. Ślad uznajemy za zamknięty, gdy skończy się korzeń i minie krótki
  czas na spóźnione spany.
- Metryki: szeregi czasowe z agregacją w kilku rozdzielczościach, np. 10 s przez 1 h, 1 min przez 24 h,
  10 min przez 7 dni. Limit liczby szeregów na metrykę (ochrona przed wysoką kardynalnością) z licznikiem
  odrzuconych.
- Błędy: grupy z licznikami w czasie, pierwszym i ostatnim wystąpieniem oraz ograniczoną listą
  ostatnich wystąpień (z linkami do śladu i logów).

Trwałość: w v1 tylko pamięć (plus pliki logów jak w LogWay). Opcjonalny magazyn trwały (SQLite lub
PostgreSQL) to osobna decyzja na później, ważna głównie dla Logle.

## 6. Błędy (exceptions)

Źródła wyjątków:
- zdarzenia `exception` na spanach (`Activity.AddException`, instrumentacja ASP.NET Core z `RecordException`),
- logi z wyjątkiem,
- spany ze statusem error bez wyjątku pokazujemy w śladach, ale nie tworzą grupy błędu.

Grupowanie (fingerprint): typ wyjątku + znormalizowane najwyższe ramki stosu z kodu aplikacji (bez ramek
frameworka). Wiadomość nie wchodzi do odcisku, bo zawiera zmienne dane (id, ścieżki). Nazwa operacji wchodzi
tylko wtedy, gdy wyjątek nie ma ramek (zmiana w M2: log i span tego samego wyjątku często widzą różne operacje,
więc z operacją w odcisku nie dałoby się ich policzyć raz).
Ten sam wyjątek zgłoszony przez log i przez span w tym samym śladzie liczymy raz.

Widok: lista grup (liczba, trend, ostatnio), szczegóły ze stosem, wystąpienia, przejście do śladu i logów
żądania, w którym wystąpił.

## 7. Powiązania między sygnałami

- log -> ślad i ślad -> logi (trace id; jak filtr `trace:` w LogWay),
- błąd -> ślad i logi wystąpienia,
- punkt metryki -> przykładowy ślad (exemplar),
- przegląd -> kliknięcie w skok błędów lub opóźnień otwiera listę śladów z tego przedziału.

## 8. UI (Way.Shell od początku)

Moduł Way.Shell (`WayModule("monitor")`), menu w powłoce, i18n en i pl.

- Przegląd: dla każdej usługi i trasy HTTP liczba żądań, odsetek błędów i opóźnienia p50, p95, p99
  (z `http.server.request.duration`), ostatnie błędy, najwolniejsze trasy.
- Logi: przeniesiony widok LogWay (lista, minimapa, wyszukiwanie, live tail, źródła plikowe).
- Ślady: lista z filtrami (usługa, operacja, status, czas trwania, atrybut), szczegóły jako wykres
  wodospadowy z atrybutami i zdarzeniami spanów oraz logami śladu.
- Metryki: lista instrumentów, wykres wybranej metryki z grupowaniem po atrybucie, percentyle dla
  histogramów.
- Błędy: jak w sekcji 6.

Wyszukiwanie: składnia LogWay rozszerzona o pola spanów, np. `service:shop status:error duration:>500ms
http.route:/orders/{id}`.

Wykresy: Way.Shell nie udostępnia biblioteki wykresów, więc trafi do paczki modułu. Kandydaci: uPlot
(mały, szybki, ok. 50 kB) albo własne SVG dla prostych wykresów. MUI X Charts jest za duży. Decyzja w M3.

## 9. Paczki

| Paczka | Zawartość |
|---|---|
| `Kododo.MonitorWay.Core` | model, publiczne abstrakcje magazynów i źródeł, zapytania |
| `Kododo.MonitorWay` | builder, magazyny w pamięci, eksportery OTel, grupowanie błędów, wyszukiwanie |
| `Kododo.MonitorWay.UI` | moduł Way.Shell, handlery Reiho, SSE |
| `Kododo.MonitorWay.Files` | źródła plikowe logów z LogWay (dla Logle i aplikacji piszących logi do plików) |
| `Kododo.MonitorWay.Mcp` | narzędzia MCP dla wszystkich sygnałów |
| `Kododo.MonitorWay.Otlp` | odbiornik OTLP/HTTP (faza M5) |

MCP (stan po M4 w §17): `monitorway_get_service_overview`, logi, `get_traces`, `get_trace`, `get_errors`,
`get_error`, `get_metrics`, `get_metric_series` i statystyki. Agent AI może wtedy sam przejść od błędu przez ślad do logów działającej aplikacji.

Bezpieczeństwo: jak w rodzinie (403 poza Development bez decyzji, autoryzacja grupy), Way oznaczony jako
wrażliwy w Way.Shell. Atrybuty mogą zawierać dane osobowe; opcja maskowania wybranych kluczy (np.
`http.request.header.authorization`) przed zapisem.

## 10. Logle

Logle przechodzi z LogWay na MonitorWay (`Files` + `UI`, zmiana w dwóch plikach). Po fazie M5 dostaje
odbiornik OTLP: usługi z serwera wysyłają do Logle ślady, metryki i logi, a nie tylko logi z plików Dockera.
Wtedy wraca pytanie o trwały magazyn (SQLite na `/data`), bo dane z OTLP nie mają plików jako zapasu.

## 11. Fazy

| Faza | Stan | Zakres |
|---|---|---|
| M0 | gotowe | Repo, szkielet paczek, Way.Shell, model w Core; przeniesienie kodu LogWay (bufor, pliki, wyszukiwanie, lista w SPA); logi przez eksporter OTel |
| M1 | gotowe | Ślady: eksporter, magazyn z retencją po zakończeniu śladu, lista, wykres wodospadowy, przejście z logu do śladu i z powrotem |
| M2 | gotowe | Błędy: grupowanie, widok, powiązania |
| M3 | gotowe | Metryki: eksporter, szeregi w kilku rozdzielczościach, widok metryk, przegląd (żądania, błędy, opóźnienia) |
| M4 | gotowe | MCP dla wszystkich sygnałów |
| M5 | gotowe | Odbiornik OTLP/HTTP, filtr usług w UI |
| M6 | odłożone | 2026-10-25: Logle nie przechodzi na MonitorWay, w jego miejsce będzie inny produkt (do przemyślenia); decyzja o trwałym magazynie razem z nim |

Każda faza kończy się demo i testami (jednostkowe, integracyjne z prawdziwym SDK OTel, e2e w przeglądarce).

## 12. Decyzje do podjęcia

1. Zbieranie przez SDK OpenTelemetry (rekomendacja) czy własne listenery bez zależności od SDK.
2. Odbiornik OTLP w pierwszym wydaniu czy później (rekomendacja: M5, przed wydaniem 1.0/2.0, bo to on
   daje zgodność z OTel "z zewnątrz" i jest potrzebny Logle).
3. Trwały magazyn: tylko pamięć w v1 (rekomendacja) czy od razu baza.
4. Wersja: rodzina jest na wersji głównej 2 (Way.Shell, ConfigWay). Start od `2.0.0-preview` dla spójności
   czy od `1.0.0-preview`.
5. Biblioteka wykresów (M3): uPlot (decyzja 2026-10-25).

## 13. Stan M0 (2026-10-21)

Zrobione:

- Kod LogWay przeniesiony i przemianowany (`Kododo.MonitorWay*`), bez adapterów Serilog i NLog. Opis LogWay
  zostaje w repo LogWay.
- Zbieranie logów przez SDK OpenTelemetry 1.20.1: `LogExporter : BaseExporter<LogRecord>` z procesorem
  synchronicznym (live tail bez opóźnienia), rejestracja `WithLogging(l => l.AddMonitorWay())` w przestrzeni
  `OpenTelemetry.Logs`. `AddMonitorWay()` zawsze tworzy bufor i włącza w opcjach OTel `IncludeFormattedMessage`
  oraz `IncludeScopes` (wiadomość, zakresy, `RequestPath` do pomijania własnych żądań). Własny
  `ILoggerProvider` usunięty. Poziom z `LogRecord.LogLevel` (`Severity` w 1.20 nie jest publiczne).
- Model: `LogEntry` dostał `ExceptionType` i `ServiceName` (z `service.name` zasobu OTel), opcjonalne na końcu,
  więc czytnik plików się nie zmienił.
- Źródło "Memory" jest na liście tylko wtedy, gdy OTel eksportuje do bufora (host tylko z plikami go nie widzi).
- UI w Way.Shell 2.1.1: `WayModule("monitor")` z `IsSensitive`, ścieżka `/monitor`, ochrona 403 z powłoki;
  tytuł, użytkownik i `AllowAnonymous` przeszły do `ShellOptions` (`GetUiSettings`, `MonitorWayUser` usunięte).
  MCP pod `/monitor/mcp` z tą samą regułą (w tym polityka domyślna i `ShellOptions.AllowAnonymous`).
- SPA jako moduł (`wayModule()`, `defineWay`, `WayDevHost`): klient API powłoki, i18n i motyw z powłoki, stan
  widoku w parametrach trasy (`#/logs?q=...`), menu z pozycją Logs. Vitest 4 (0 podatności).
- Reiho 2.1.1, Reiho.Mcp 2.1.1, FileProviders.Embedded 10.0.13.
- Testy: 269 x net8/9/10 (w tym nowe testy eksportera z prawdziwym SDK, obok innego eksportera), MCP 6 x 3,
  Vitest 31. e2e `src/MonitorWay.Demo.Web/e2e/check.mjs`: 17/17 w Chrome, Edge, Firefox i WebKit.
- Demo na OTel (`AddService("shop")`), obraz Dockera sprawdzony lokalnie.

Przesunięte z M0: wydzielenie plików do `Kododo.MonitorWay.Files` i publiczne abstrakcje źródeł (jedynym
odbiorcą był Logle, którego na razie nie ruszamy).

## 14. Stan M1 (2026-10-25)

- Model w Core: `TraceSpan` (z `TraceSpanKind`, `TraceSpanStatus`, `SpanEvent`, `SpanLink`), `TraceEntry` (cały
  ślad: korzeń, start, czas, liczba błędów, usługi), `TraceQuery`. Nazwy z przedrostkiem `Trace`, bo `SpanKind`
  koliduje z `OpenTelemetry.Trace.SpanKind`. Atrybuty jako tekst, jak w logach (typowane wartości dopiero
  przy OTLP w M5, jeśli będą potrzebne).
- `TraceExporter : BaseExporter<Activity>` z `SimpleActivityExportProcessor`, rejestracja
  `WithTracing(t => t.AddMonitorWay())` w przestrzeni `OpenTelemetry.Trace`. Status z `Activity.Status` albo ze
  starego tagu `otel.status_code`; zdarzenia, linki, `HasRemoteParent`, `service.name` z zasobu.
- `MemoryTraceStore`: spany zbierane per ślad, zapis całego śladu po zakończeniu korzenia (spanu bez rodzica albo
  ze zdalnym rodzicem) i po `CompletionDelay` (5 s); ślad bez korzenia po 2 min. Pule `Errors`, `Slow`
  (`SlowThreshold` 1 s), `Other` z limitami liczby, bajtów i wieku; globalny `MaxTotalBytes` (64 MB) usuwa
  najpierw z `Other`. Spóźnione spany: ślad zapisywany ponownie z nowym id (stary usuwany z puli). Limity: 1000
  spanów na ślad (reszta liczona), 10 000 śladów w trakcie. Opcje: `x.ConfigureTraces(...)` i sekcja
  `MonitorWay:Traces`.
- Pomijanie własnych żądań: wspólny singleton `IgnoredRequests` dla logów i śladów (ślad rozpoznawany po
  `url.path` spanu serwera, jego dzieci giną razem z nim, spóźnione spany przez 2 min). Pomijane są też zasoby
  powłoki (`ShellOptions.AssetsPath`, domyślnie `/_way-shell`), także w logach.
- Wyszukiwanie śladów (`TraceQueryMatcher`) na składni logów: słowa, frazy, `-`, `service:`, `name:`, `kind:`,
  `status:error|ok`, `duration:>500ms` (całego śladu), `has:error|exception|<atrybut>`, `exception:`, `trace:`,
  `<atrybut>:<wartość>` z `*`.
- API: `GetTraces` (strony po id, od najnowszych), `GetTrace` (z trwającymi spanami, `isComplete`),
  `GetTraceStats`. `EntryRow` dostał `SpanId`. MCP bez zmian (ślady w M4).
- SPA: menu Ślady, lista (na żywo co 3 s, sortowana po starcie, przyciski błędów i wolnych, pasek czasu, kod
  HTTP), szczegóły `#/traces/<id>`: wodospad ze zwijaniem, znaczniki zdarzeń (wyjątki na czerwono), liczba logów
  przy spanie, panel spanu (atrybuty, zdarzenia ze stosem, linki), logi śladu lub spanu, "Pokaż w logach"
  (`trace:<id>`); w szczegółach logu "Otwórz ślad" (gdy ślady są zbierane). en i pl.
- Demo: sklep generuje zagnieżdżone spany (`ActivitySource("Kododo.Shop")`: cache, baza, magazyn, płatność,
  czasem wolna albo z wyjątkiem) plus instrumentacja ASP.NET Core.
- Testy: 319 x net8/9/10 (nowe: eksporter z prawdziwym SDK, magazyn z `FakeTimeProvider`, wyszukiwanie, API),
  MCP 6 x 3, Vitest 36. e2e: `check.mjs` 17/17 i `traces.mjs` 25/25 w Chrome, Edge, Firefox i WebKit.

Uwaga do testów: instrumentacja ASP.NET Core nasłuchuje wszystkich serwerów w procesie, więc testy API zawężają
zapytania do własnych tras (testy działają równolegle).

## 15. Stan M2 (2026-10-25)

- Model w Core: `ErrorGroup`, `ErrorOccurrence`, `ErrorQuery`.
- `ErrorFingerprint`: typ + do 5 ramek aplikacji z tekstu wyjątku (`Exception.ToString()`, to samo niesie log i
  zdarzenie spanu), ramki zewnętrznego wyjątku (po ostatnim "End of inner exception"), bez argumentów, plików i
  nazw kompilatora (async, lambdy); słowo "at" w dowolnym języku. Bez ramek aplikacji: ramki frameworka; bez
  ramek wcale: operacja. SHA-256, 16 znaków hex.
- `MemoryErrorStore`: grupy z licznikiem, pierwszym i ostatnim wystąpieniem, usługami, operacjami (z liczbą),
  trendem godzinowym na 7 dni i 50 ostatnimi wystąpieniami. Limity: 1000 grup (wypada najdawniej widziana),
  wiek 7 dni od ostatniego wystąpienia; `x.ConfigureErrors(...)`, sekcja `MonitorWay:Errors`.
- Źródła: `LogExporter` (wpis z wyjątkiem zapisany w buforze, więc żądania UI odpadają; operacja = lokalny korzeń
  `Activity.Current`) i `MemoryTraceStore` przy zamykaniu śladu (zdarzenia `exception` nowych spanów; operacja =
  lokalny korzeń w śladzie). Log i span jednego wyjątku w tym samym śladzie (w ciągu minuty) scalane w jedno
  wystąpienie; operacja brana ze spanu, bo instrumentacja ASP.NET Core nadaje trasę (`GET /pay`) dopiero na końcu
  żądania, a log widzi samo `GET`.
- API: `GetErrors` (sortowanie recent/count, wyszukiwanie `type:`, `service:`, `endpoint:`, `trace:`),
  `GetError`, `GetErrorStats`; `EntryDetail.ErrorFingerprint` i `SpanEventDto.ErrorFingerprint` do linków.
- SPA: menu Błędy, lista (typ, wiadomość, gdzie, liczba, trend 24 h, ostatnio; sortowanie w adresie), szczegóły
  `#/errors/<odcisk>` (trend 7 dni, ramki, stos z wyróżnionymi ramkami aplikacji, wystąpienia z log/span i
  przejściem do śladu i logów); "Otwórz błąd" w szczegółach logu i przy wyjątku spanu. en i pl.
- Demo: dodatkowo błędny kupon (`FormatException`, ostrzeżenie, wyjątek na spanie bez statusu błędu).
- Testy: 339 x net8/9/10 (odcisk, magazyn, API z prawdziwym SDK: log + span = jedno wystąpienie), MCP 6 x 3,
  Vitest 38. e2e: `check.mjs` 17/17, `traces.mjs` 25/25, `errors.mjs` 24/24 w Chrome, Edge, Firefox i WebKit.

## 16. Stan M3 (2026-10-25)

- Model w Core: `MetricKind`, `MetricInstrument`.
- `MetricExporter : BaseExporter<Metric>` w `PeriodicExportingMetricReader` (co 10 s, temporalność delta, własny
  czytnik niezależny od innych), rejestracja `WithMetrics(m => m.AddMonitorWay())` w `OpenTelemetry.Metrics`.
  Rodzaje: sumy monotoniczne = Counter, niemonotoniczne (w SDK zawsze skumulowane) = UpDownCounter, gauge,
  histogram z jawnymi kubełkami; wykładnicze histogramy pomijane. Licznik skumulowany zamieniany na przyrost
  (z obsługą resetu). Punkty z `http.route` żądań UI pomijane (`IgnoredRequests`).
- `MemoryMetricStore`: szereg = instrument + zestaw wartości atrybutów, trzy pierścienie: 10 s x 360 (1 h),
  1 min x 1440 (24 h), 10 min x 1008 (7 dni). Kubełek: suma, liczba, min, max, ostatnia, średnia, liczności
  histogramu. Limity: 500 szeregów na instrument i 64 MB łącznie (pamięć szeregu rezerwowana z góry na pełne
  pierścienie), nadmiar liczony; szeregi bez pomiarów przez 7 dni usuwane. `x.ConfigureMetrics(...)`, sekcja
  `MonitorWay:Metrics` (także `ExportInterval`).
- `Aggregate`: najdrobniejsza rozdzielczość pokrywająca zakres, grupowanie po kluczach atrybutów (scalanie
  sum i histogramów; gauge: suma średnich szeregów), filtr szeregów, największe grupy. `MetricStats`: sum/rate,
  avg/last/min/max, p50-p99 z kubełków (interpolacja liniowa, w granicach min/max), avg/max/count/rate histogramu.
- API: `GetMetrics`, `GetMetricSeries` (statystyka, `percentiles` = p50/p95/p99, groupBy, filter),
  `GetServiceOverview` (z `http.server.request.duration`: żądania, błędy 5xx lub `error.type`, p50/p95/p99 w czasie
  i per trasa), `GetMetricStats`. Nazwa `GetServiceOverview`, bo `GetOverview` to pasek przeglądu logów.
- SPA: uPlot 1.6.32 we własnym komponencie `TimeChart` (oś czasu, legenda z wartościami, słupki dla liczności).
  Zaznaczanie zakresu na wykresie własne, na zdarzeniach `pointer`: wbudowane przeciąganie uPlot nie reaguje w
  WebKit (sprawdzone w Playwright), a `pointer` działa też palcem. Przegląd jako strona startowa (`#/overview`):
  kafelki, wykres żądań i czasów, trasy (klik = ślady trasy w zakresie), ostatnie błędy; przeciągnięcie = ślady
  z tego czasu (lista śladów dostała `from`/`to` w adresie). Metryki: lista instrumentów po meterach, wykres z
  wyborem statystyki, grupowania i zakresu (przeciągnięcie = przybliżenie), stan w adresie. en i pl.
- Demo: symulator woła sklep przez HTTP (`/shop/orders`, `/shop/products/{slug}`, bez nagłówków śladu, więc
  korzeniem jest span serwera ASP.NET Core), metryki ASP.NET Core, runtime i sklepu (`shop.orders`,
  `shop.order.total`, `shop.carts.open`).
- Bez exemplarów (punkt metryki -> ślad): zamiast nich przejście z zakresu wykresu do listy śladów.
- Przegląd jest dla jednej usługi (aplikacji); podział na usługi wróci z OTLP w M5.
- Testy: 355 x net8/9/10 (magazyn, statystyki, eksporter z prawdziwym SDK, API z instrumentacją ASP.NET Core),
  MCP 6 x 3, Vitest 41. e2e: `metrics.mjs` 27/27, `errors.mjs` 24/24, `traces.mjs` 25/25, `check.mjs` 17/17 w
  Chrome, Edge, Firefox i WebKit. Obraz Dockera demo po zmianie na HTTP nie był sprawdzany.

## 17. Stan M4 (2026-10-25)

- Narzędzia MCP z requestów API, jak dotąd (nazwa = request w snake_case z przedrostkiem `monitorway_`), zamiast
  nazw roboczych z §9 (`find_traces`, `query_metric` itd.): jedna konwencja dla UI, MCP i dokumentacji. 16
  narzędzi, wszystkie tylko do odczytu: `get_service_overview`; logi (6 jak w M0); `get_traces`, `get_trace`,
  `get_trace_stats`; `get_errors`, `get_error`, `get_error_stats`; `get_metrics`, `get_metric_series`,
  `get_metric_stats`.
- Opisy pod agenta: jednostki (ms w śladach, sekundy w przeglądzie, jednostka instrumentu w metrykach), skąd brać
  identyfikatory, jak przejść dalej (logi śladu: źródło `memory` z `trace:<id>`), że przegląd to punkt startu, że
  przy metrykach na jedną liczbę wystarczy `total`. Domyślne `count` śladów i błędów 30 (UI podaje własne).
- Test agenta na prawdziwym SDK (`AgentInvestigationTests`): przegląd -> trasa z błędami -> grupa błędu (log i span
  liczone raz) -> ślad wystąpienia -> logi żądania -> wyszukanie nieudanych śladów; drugi: metryki.
- Przy okazji: `MultiLineTests` czasem nie przechodził w pełnym przebiegu (3 TFM naraz), bo czytnik plików ma budżet
  300 ms na skan i oddawał część wyników; test dostał dłuższy budżet (nie dotyczy budżetu).
- Testy: 355 x net8/9/10, MCP 8 x 3.

## 18. Stan M5 (2026-10-25)

- Paczka `Kododo.MonitorWay.Otlp`: typy z `opentelemetry-proto` v1.11.0 (pliki w `Protos/` z licencją Apache 2.0,
  także w paczce) generowane przy budowaniu przez Grpc.Tools, tylko komunikaty, `internal`. Zależności: Google.Protobuf
  i rdzeń MonitorWay (bez UI i Way.Shell).
- `MapMonitorWayOtlp("/monitor/otlp")`: `/v1/traces`, `/v1/logs`, `/v1/metrics`, protobuf i JSON, gzip, do 32 MB;
  415 dla innego typu treści, 400 dla błędnej wiadomości (zwykły tekst; status protobuf `google.rpc.Status` nie
  jest generowany). 403 poza Development bez decyzji (autoryzacja, `AllowAnonymous()`, polityka domyślna); bez
  `ShellOptions`, bo paczka nie zależy od powłoki. Własne żądania do odbiornika pomijane (`IgnoredRequests`).
- OTLP/JSON: identyfikatory szesnastkowe zamieniane na base64 przed parserem protobuf (jedyna różnica mapowania).
- `OtlpIngest` do tych samych magazynów: spany (rodzaj, status, zdarzenia, linki; zdalny rodzic z flag spanu, a
  bez flag dla spanów Server/Consumer z rodzicem), logi (severity_number i tekst, `{OriginalFormat}`, `exception.*`
  -> wyjątek i błąd), metryki (sumy, gauge, histogramy; wykładnicze i summary pomijane). Skumulowane histogramy
  zamieniane na przyrosty w magazynie (z resetem; min/max odrzucane, bo dotyczą całego procesu).
- `service.name` jest atrybutem każdego szeregu metryk (także własnej aplikacji), `ServiceDirectory` zbiera usługi
  i ich sygnały (do 200). `service:` działa teraz także w logach. API `GetServices`, przegląd z filtrem usługi i
  usługą przy trasie; MCP `get_services` (17 narzędzi).
- UI: wybór usługi (gdy jest więcej niż jedna): przegląd i metryki przez `svc` w adresie, ślady, błędy i logi
  przez `service:` w wyszukiwaniu. Kliknięcie trasy w przeglądzie dodaje usługę do wyszukiwania śladów.
- Odcisk błędu: eksporter OTLP logów .NET podaje krótką nazwę typu (`IOException`), span pełną; pełna nazwa jest
  brana z pierwszej linii tekstu wyjątku, więc log i span jednego wyjątku dają jedną grupę.
- Demo: druga usługa `warehouse` z własnym SDK i eksporterem OTLP (ślady, logi, metryki skumulowane co 10 s) do
  odbiornika demo; rezerwacja sklepu woła magazyn, więc ślad przechodzi przez dwie usługi. Odbiornik w demo
  przyjmuje tylko połączenia z tej maszyny (publiczne demo).
- Testy eksportera OTLP z SDK wymagają handlera z synchronicznym `Send` (eksporter tak wysyła, TestServer nie
  obsługuje).
- Testy: 360 x net8/9/10, MCP 8 x 3, OTLP 11 x 3 (prawdziwy eksporter: ślady, logi, metryki skumulowane, ślad
  dwóch usług; JSON, gzip, błędne żądania, 403), Vitest 42. e2e: `services.mjs` 17/17, `metrics.mjs` 27/27,
  `errors.mjs` 24/24, `traces.mjs` 25/25, `check.mjs` 17/17 w Chrome, Edge, Firefox i WebKit. Obraz Dockera demo
  nadal niesprawdzony (Docker Desktop nie był uruchomiony).
