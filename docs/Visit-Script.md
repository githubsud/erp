# Shop visit script (10 minutes)

For sales visits to small shops in Doha / Al Aziziya. Lead with the owner's problems (WPS fines, not knowing the day's
numbers, surprise expiries), not with "ERP". Show their own kind of business, on a phone or tablet, in Arabic.

## The evening before

1. **Phone or tablet ready.** Charged, Tailscale on, and the ERP opened once at `https://asus-a.tail754685.ts.net`
   (it is also installed as an app). The laptop must be on and connected; the ERP starts by itself.
2. **Pick the demo business** that matches the shop you are visiting:

   | Shop | Company code | What to show |
   |---|---|---|
   | Abaya / tailor | `abaya-tailor` | Orders with measurements, deposits, late orders |
   | Ladies' salon | `salon` | Today's bookings, walk-in checkout, commission |
   | Grocery / mini-mart | `grocery` | The till on the phone, credit customers, daily summary |
   | Restaurant / cafeteria | `restaurant` | Till, staff shifts, payroll |
   | Typing / PRO office | `typing-office` | Document expiries, payroll and WPS |
   | Furniture | `furniture` | Sales orders, deliveries, customer balances |
   | Car workshop | `workshop` | Job cards, parts stock |
   | Contractor | `contracting` | Projects, subcontracts, payroll |

   Sign in with the company code, `owner@<code>.qa` and `Demo-Password-2026`.
3. **Fresh dates (optional).** The demo's "today" figures come from the day the data was made. `scripts\demo-reset.ps1`
   rebuilds the demo with today's dates in the development copy (http://localhost:5081). The installed ERP keeps the
   demo data imported on 21/10/2026; ask for a refresh step if its dates feel old.
4. **Paper.** 10 printed leaflets (`docs/Sales-Leaflet.html`, print A4 from the browser) with your name and WhatsApp
   filled in.

## The 10 minutes

**0:00 – Opening (do not open the phone yet)**

> «السلام عليكم. نساعد المحلات تدفع الرواتب في وقتها عبر نظام حماية الأجور بدون غرامات، وتعرف مبيعات المحل كل مساء على جوالها. ممكن أوريك دقيقتين؟»
>
> *Peace be upon you. We help shops pay salaries on time through WPS without fines, and see the shop's sales every
> evening on the phone. Can I show you for two minutes?*

Ask one question before showing anything: **«كيف تدفعون الرواتب الآن؟ ومن يجهز ملف حماية الأجور؟»**
(*How do you pay salaries now, and who prepares the WPS file?*) Listen; their answer tells you what to show first.

**0:30 – The evening view (dashboard on the phone)**

Open the dashboard. Point at **ملخص اليوم** (today's summary) and the figures under it.

> «هذا ما يصلك كل مساء: مبيعات اليوم، النقد في الصندوق، فرق الوردية، ومن عليه ديون.»
>
> *This is what reaches you every evening: today's sales, cash in the till, the shift difference, and who owes you money.*

**2:00 – The till (grocery, restaurant, any shop with a counter)**

Point of sale → resume the shift → tap three items → **نقداً بالضبط** (exact cash) → **دفع** (pay).
Then switch on airplane mode, sell one more item, and show that it is kept; switch it off and it is sent.

> «انقطع الإنترنت؟ البيع ما يتوقف.»  *Internet down? Selling does not stop.*

**3:30 – Their own business**

- *Tailor:* Tailoring orders → a late order in red, the deposit and balance, each tailor's load.
  «كل طلب بمقاساته وعربونه وموعد تسليمه، والمتأخر يظهر بالأحمر.»
- *Salon:* Salon → today's grid by stylist → **دفع زائر بدون موعد** (walk-in checkout) → Commission tab.
  «عمولة كل موظفة محسوبة، بدون دفتر.»
- *Typing office:* Documents → the list of QIDs, CR and licences about to expire.
  «قبل ما تنتهي البطاقة أو الرخصة بثلاثة أشهر، يصلك تذكير.»

**5:30 – Payroll and WPS (every shop with staff)**

Payroll → open the latest month → point at **آخر موعد لحماية الأجور** (the WPS deadline) → the employees and net pay.
If the run is approved, press **إنشاء ملف حماية الأجور** to show the SIF file appear.

> «الرواتب تُحسب من العقد، والملف جاهز للبنك. ما في غرامات تأخير.»
>
> *Salaries come from each contract, and the file is ready for the bank. No late fines.*

**7:00 – Ask the assistant (only if the owner is interested)**

**اسأل المساعد الذكي** → type «من أكثر عميل مديون؟» (who owes me the most?). Keep it to one question.

**8:00 – Price and the offer**

Hand over the leaflet. Shop plan **199 ريال شهرياً** (payroll only: 99 up to 10 staff).

> «أول عشر محلات في العزيزية: ثلاثة أشهر مجاناً، ونجهز النظام وندخل بياناتكم مجاناً.»
>
> *The first ten shops in Al Aziziya: three months free, and we set up the system and enter your data for free.*

**9:00 – Close with questions, not a hard sell**

Ask 2–3 of the validation questions (strategy document, section 9), for example:
«لو النظام شال عنك مشكلة الرواتب والرخص، تدفع 199 ريال بالشهر؟ وإيش ممكن يخليك تقول لا؟»
(*If this took payroll and renewals off your hands, would you pay 199 a month? What would make you say no?*)

Write down the answers right after leaving.

## Common objections

| They say | You answer |
|---|---|
| «غالي» (*too expensive*) | Compare with one WPS fine or one month of a part-time accountant. Start with the Payroll plan (99) and add the till later. |
| «عندي Excel» (*I use Excel*) | Keep it: their price list goes in from Excel in one step, and everything comes out to Excel any time. |
| «الإنترنت ضعيف» (*weak internet*) | Show the airplane-mode sale again. |
| «المحاسب يسوي الرواتب» (*my accountant does payroll*) | The accountant can sign in too and spends less time; the owner still sees everything. |
| «خايف على بياناتي» (*worried about my data*) | Personal numbers are encrypted, every user has their own login and permissions, a backup is made every night, and the data can be exported at any time. |
| «الموظفين ما يقرون عربي» (*staff don't read Arabic*) | Every screen switches to English with one button. |

## After the visit

- Note: shop type, number of staff, what they use today, the answers to the validation questions, and a follow-up date.
- If they agreed to a trial: create them as a customer in the back-office (`/backoffice` → Customers), then on the
  first day use the Getting started list with them and import their price list from Excel.
- Anything they asked for that the ERP cannot do: send it as a suggestion from Help → New request, so it is counted
  in the back-office's suggestions list.
