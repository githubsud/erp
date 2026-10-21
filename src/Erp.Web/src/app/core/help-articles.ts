/**
 * Short how-to articles for shop owners and staff, in both languages. Shown on the Help page and suggested while writing a
 * support request. Screen and button names must match the app's own labels (en.ts / ar.ts).
 */
export interface HelpArticle {
  id: string;
  icon: string;
  /** Extra words people may search with (both languages). */
  keywords: string;
  titleEn: string;
  titleAr: string;
  /** Paragraphs; a paragraph starting with "1." etc. is shown as a step. */
  en: string[];
  ar: string[];
}

export const HELP_ARTICLES: HelpArticle[] = [
  {
    id: 'first-day', icon: 'flag', keywords: 'start setup begin new shop البداية إعداد جديد',
    titleEn: 'Your first day: setting up the shop', titleAr: 'يومك الأول: تجهيز المحل',
    en: [
      'The "Getting started" list on the dashboard shows what is left, in order. Each step opens the right screen.',
      '1. Quick setup: choose your kind of business; it switches on what you need and offers a starter price list.',
      '2. Company profile: add phone, address and CR number; they are printed on every receipt.',
      '3. Items: add your products and prices, or import your price list from Excel with the opening stock.',
      '4. Point of sale: create a till, set up the receipt printer and make a test sale.',
      '5. Users & roles: give each person their own login.',
      'You can hide the list when you are done, and bring it back from the Help menu.'
    ],
    ar: [
      'قائمة "البداية" في لوحة المعلومات تعرض ما تبقى بالترتيب، وكل خطوة تفتح الشاشة المناسبة.',
      '1. الإعداد السريع: اختر نوع نشاطك؛ فيُفعّل ما تحتاجه ويقترح قائمة أسعار أولية.',
      '2. بيانات الشركة: أضف الهاتف والعنوان ورقم السجل التجاري؛ فهي تُطبع على كل إيصال.',
      '3. الأصناف: أضف منتجاتك وأسعارها، أو استورد قائمة أسعارك من إكسل مع المخزون الافتتاحي.',
      '4. نقطة البيع: أنشئ نقطة بيع، وجهّز طابعة الإيصالات، وجرّب عملية بيع.',
      '5. المستخدمون والأدوار: أنشئ دخولاً خاصاً لكل شخص.',
      'يمكنك إخفاء القائمة عند الانتهاء، وإظهارها مرة أخرى من قائمة المساعدة.'
    ]
  },
  {
    id: 'import-excel', icon: 'upload_file', keywords: 'excel import price list upload xlsx استيراد اكسل قائمة أسعار رفع',
    titleEn: 'Import items, customers and suppliers from Excel', titleAr: 'استيراد الأصناف والعملاء والموردين من إكسل',
    en: [
      'On the Items, Customers or Suppliers page press "Import from Excel".',
      '1. Download the template. Fill one row per item (or customer, supplier) from row 3; dark green columns are required.',
      '2. Choose your file and press Check. Nothing is saved yet: every problem is listed with its row number.',
      '3. Fix the file, check again, then press Import. The whole file is saved together, or nothing is.',
      'Importing again with the same codes updates them (for example new prices); empty cells keep what is there.',
      'Opening quantities are taken in once, for items that have no stock movements yet; later changes are made with a stock count.'
    ],
    ar: [
      'في صفحة الأصناف أو العملاء أو الموردين اضغط "استيراد من إكسل".',
      '1. نزّل القالب. أدخل صنفاً واحداً (أو عميلاً أو مورداً) في كل صف ابتداءً من الصف الثالث؛ الأعمدة الخضراء الداكنة إلزامية.',
      '2. اختر ملفك واضغط تحقق. لا يُحفظ شيء بعد: تظهر كل مشكلة مع رقم صفها.',
      '3. صحّح الملف وتحقق مرة أخرى، ثم اضغط استيراد. يُحفظ الملف كاملاً أو لا يُحفظ شيء.',
      'الاستيراد مرة أخرى بنفس الرموز يحدّثها (مثل الأسعار الجديدة)؛ والخلايا الفارغة تبقي القيم الحالية.',
      'الكميات الافتتاحية تُضاف مرة واحدة للأصناف التي ليس لها حركات مخزون؛ والتعديلات بعد ذلك تكون بالجرد.'
    ]
  },
  {
    id: 'pos-sale', icon: 'point_of_sale', keywords: 'sell till cashier barcode scan payment بيع كاشير باركود دفع',
    titleEn: 'Selling at the till', titleAr: 'البيع في نقطة البيع',
    en: [
      '1. Open Point of sale and choose your till. If no shift is open, press "Open shift" and enter the cash in the drawer.',
      '2. Scan the barcode, or search and tap the item. Change the quantity on the line if needed.',
      '3. Press Pay, choose Cash or Card (or both), and enter the amount received; the change is shown.',
      '4. The receipt prints by itself if automatic printing is on in the printer settings.',
      'Each till has its own receipt numbers, so two counters never share a number.'
    ],
    ar: [
      '1. افتح نقطة البيع واختر نقطتك. إذا لم تكن هناك وردية مفتوحة اضغط "فتح وردية" وأدخل النقد الموجود في الدرج.',
      '2. امسح الباركود، أو ابحث عن الصنف واضغط عليه. عدّل الكمية في السطر عند الحاجة.',
      '3. اضغط دفع، واختر نقداً أو بطاقة (أو الاثنين)، وأدخل المبلغ المستلم؛ يظهر الباقي.',
      '4. يُطبع الإيصال تلقائياً إذا كانت الطباعة التلقائية مفعّلة في إعدادات الطابعة.',
      'لكل نقطة بيع أرقام إيصالات خاصة بها، فلا يتكرر رقم بين كاونترين.'
    ]
  },
  {
    id: 'pos-shift', icon: 'savings', keywords: 'shift close cash count difference drawer end of day وردية إغلاق عد النقد فرق نهاية اليوم',
    titleEn: 'Opening and closing a shift', titleAr: 'فتح الوردية وإغلاقها',
    en: [
      'A shift is one cashier\'s time at a till. Open it with the cash already in the drawer.',
      'At the end, press "Close shift" and count the cash. The system shows what should be there; any difference is recorded and appears on the dashboard.',
      'Close every shift before going home: an open shift keeps adding the next day\'s sales to the same totals.'
    ],
    ar: [
      'الوردية هي فترة عمل كاشير على نقطة بيع. افتحها بالنقد الموجود في الدرج.',
      'في النهاية اضغط "إغلاق الوردية" وعدّ النقد. يعرض النظام المبلغ المتوقع؛ وأي فرق يُسجّل ويظهر في لوحة المعلومات.',
      'أغلق كل وردية قبل المغادرة: الوردية المفتوحة تضيف مبيعات اليوم التالي إلى نفس المجاميع.'
    ]
  },
  {
    id: 'pos-refund', icon: 'undo', keywords: 'refund return exchange money back استرداد إرجاع استبدال',
    titleEn: 'Refunds and returns', titleAr: 'الاسترداد والإرجاع',
    en: [
      'At the till press Refund, type the receipt number and press Search, enter the quantities being returned, and pay the customer back in cash (or switch on refund to card).',
      'Returned items go back into stock and the refund gets its own number. Refunds need the internet.'
    ],
    ar: [
      'في نقطة البيع اضغط استرداد، واكتب رقم الإيصال واضغط بحث، وأدخل الكميات المرتجعة، ثم أعد المبلغ للعميل نقداً (أو فعّل الاسترداد إلى البطاقة).',
      'تعود الأصناف المرتجعة إلى المخزون ويأخذ الاسترداد رقماً خاصاً به. الاسترداد يحتاج إلى الإنترنت.'
    ]
  },
  {
    id: 'offline', icon: 'cloud_off', keywords: 'internet down offline no connection sync إنترنت منقطع بدون اتصال مزامنة',
    titleEn: 'When the internet is down', titleAr: 'عند انقطاع الإنترنت',
    en: [
      'The till keeps selling for cash or card. Receipts print marked "saved without internet" and a counter shows how many sales are waiting.',
      'When the connection is back, the waiting sales are sent by themselves, once each. Keep the till open until the counter is gone.',
      'Refunds, sales on account and opening a new shift wait for the internet.'
    ],
    ar: [
      'تستمر نقطة البيع في البيع نقداً أو بالبطاقة. تُطبع الإيصالات مع عبارة "حُفظ بدون إنترنت" ويظهر عدد المبيعات المنتظرة.',
      'عند عودة الاتصال تُرسل المبيعات المنتظرة تلقائياً، كل منها مرة واحدة. أبقِ نقطة البيع مفتوحة حتى يختفي العدد.',
      'الاسترداد والبيع بالآجل وفتح وردية جديدة تنتظر عودة الإنترنت.'
    ]
  },
  {
    id: 'printer', icon: 'print', keywords: 'printer receipt paper 80mm 58mm drawer kiosk طابعة إيصال ورق درج',
    titleEn: 'Receipt printer and cash drawer', titleAr: 'طابعة الإيصالات ودرج النقود',
    en: [
      '1. Connect the printer to the till computer and install its Windows driver.',
      '2. On the Point of sale page press the printer button: choose 80 mm, 58 mm or A4, turn on automatic printing, and print a test.',
      '3. To print without the print window, start Chrome with the kiosk printing option (shown in the printer settings).',
      '4. A cash drawer plugged into the printer opens with each receipt when "open drawer" is set in the printer driver.',
      'Your shop\'s message at the bottom of receipts is set in Company profile → Receipts.'
    ],
    ar: [
      '1. صِل الطابعة بجهاز نقطة البيع وثبّت تعريفها على ويندوز.',
      '2. في صفحة نقطة البيع اضغط زر الطابعة: اختر 80 مم أو 58 مم أو A4، وفعّل الطباعة التلقائية، واطبع تجربة.',
      '3. للطباعة بدون نافذة الطباعة، شغّل كروم بخيار الطباعة المباشرة (موضح في إعدادات الطابعة).',
      '4. درج النقود الموصول بالطابعة يُفتح مع كل إيصال عند تفعيل "فتح الدرج" في تعريف الطابعة.',
      'رسالة محلك في أسفل الإيصال تُضبط من بيانات الشركة ← الإيصالات.'
    ]
  },
  {
    id: 'install-app', icon: 'install_mobile', keywords: 'app phone install home screen android iphone تطبيق جوال تثبيت',
    titleEn: 'Install the app on your phone', titleAr: 'تثبيت التطبيق على الجوال',
    en: [
      'Android: open the ERP in Chrome, sign in, then choose "Install the app on this device" from your account menu (or Chrome\'s menu → Install app).',
      'iPhone: open the ERP in Safari, press Share, then "Add to Home Screen".',
      'Open it from the new icon. Tick "Keep me signed in on this device" on your own phone.'
    ],
    ar: [
      'أندرويد: افتح النظام في كروم وسجّل الدخول، ثم اختر "ثبّت التطبيق على هذا الجهاز" من قائمة حسابك (أو قائمة كروم ← تثبيت التطبيق).',
      'آيفون: افتح النظام في سفاري، اضغط مشاركة، ثم "إضافة إلى الشاشة الرئيسية".',
      'افتحه من الأيقونة الجديدة. فعّل "تذكرني على هذا الجهاز" على جوالك الشخصي.'
    ]
  },
  {
    id: 'users', icon: 'manage_accounts', keywords: 'user login staff cashier role permission password مستخدم دخول موظف كاشير صلاحية كلمة مرور',
    titleEn: 'Adding staff and choosing what they can do', titleAr: 'إضافة الموظفين وتحديد صلاحياتهم',
    en: [
      'Open Users & roles → New user. Give the person a login and a role: for example Sales Executive for a cashier, Storekeeper for stock, Accountant for the books.',
      'A role decides which screens and buttons they see; you can change it any time. Owners should turn on two-step sign-in.',
      'When someone leaves, switch their user off instead of sharing passwords.'
    ],
    ar: [
      'افتح المستخدمون والأدوار ← مستخدم جديد. أعطِ الشخص دخولاً ودوراً: مثلاً مسؤول المبيعات للكاشير، وأمين المخزن للمخزون، والمحاسب للحسابات.',
      'الدور يحدد الشاشات والأزرار التي يراها، ويمكنك تغييره في أي وقت. ننصح المالك بتفعيل الدخول بخطوتين.',
      'عند مغادرة شخص، أوقف مستخدمه بدلاً من مشاركة كلمات المرور.'
    ]
  },
  {
    id: 'employees', icon: 'badge', keywords: 'employee qid passport expiry documents visa موظف بطاقة جواز انتهاء مستندات',
    titleEn: 'Employees and their documents', titleAr: 'الموظفون ومستنداتهم',
    en: [
      'Add each employee with salary, QID and IBAN, or import them all from Excel on the Employees page.',
      'Add the expiry dates of QID, passport, work permit and health card: the dashboard and an email warn you before they expire.',
      'Employees can get their own login to see payslips and ask for leave from their phone.'
    ],
    ar: [
      'أضف كل موظف مع الراتب والرقم الشخصي والآيبان، أو استوردهم جميعاً من إكسل في صفحة الموظفين.',
      'أضف تواريخ انتهاء البطاقة الشخصية والجواز وتصريح العمل والبطاقة الصحية: تنبهك لوحة المعلومات ورسالة بريد قبل انتهائها.',
      'يمكن للموظفين الحصول على دخول خاص لرؤية قسائم الرواتب وطلب الإجازات من الجوال.'
    ]
  },
  {
    id: 'payroll', icon: 'payments', keywords: 'payroll salary wps sif bank payslip month رواتب راتب حماية الأجور بنك قسيمة',
    titleEn: 'Monthly payroll and the WPS file', titleAr: 'الرواتب الشهرية وملف حماية الأجور',
    en: [
      '1. Payroll → New payroll run for the month. Add overtime, deductions and advances first if any.',
      '2. Calculate, check the totals, then Approve and Post to accounts.',
      '3. Make the WPS (SIF) file and upload it to your bank before the deadline shown on the dashboard.',
      '4. When the bank has paid, press Mark as paid. Payslips can be emailed to employees.',
      'Please confirm overtime and deduction rules with your payroll adviser before the first real payroll.'
    ],
    ar: [
      '1. الرواتب ← مسيرة رواتب جديدة للشهر. أضف العمل الإضافي والخصومات والسلف أولاً إن وجدت.',
      '2. اضغط احتساب وراجع المجاميع، ثم اعتماد، ثم ترحيل للحسابات.',
      '3. أنشئ ملف حماية الأجور (SIF) وارفعه إلى بنكك قبل الموعد الظاهر في لوحة المعلومات.',
      '4. بعد أن يصرف البنك الرواتب اضغط تسجيل الصرف. ويمكن إرسال القسائم للموظفين بالبريد.',
      'يرجى التأكد من قواعد العمل الإضافي والخصومات مع مستشار الرواتب قبل أول مسيرة حقيقية.'
    ]
  },
  {
    id: 'stock-count', icon: 'inventory', keywords: 'stock count quantity wrong adjust damage expiry جرد كمية خطأ تعديل تالف منتهي',
    titleEn: 'Counting stock and fixing quantities', titleAr: 'جرد المخزون وتصحيح الكميات',
    en: [
      'Stock counts: start a count (for the whole shop or one group), enter what is on the shelves, and post it. The differences are adjusted and valued for you.',
      'For damaged or expired goods use a stock adjustment with that reason, so your losses show in the reports.'
    ],
    ar: [
      'الجرد: ابدأ جرداً (للمحل كله أو لمجموعة)، وأدخل الموجود على الرفوف، ثم رحّله. تُعدّل الفروقات وتُقيّم تلقائياً.',
      'للبضاعة التالفة أو المنتهية استخدم تسوية مخزون بذلك السبب، لتظهر خسائرك في التقارير.'
    ]
  },
  {
    id: 'credit', icon: 'account_balance_wallet', keywords: 'credit on account debt owe collect payment reminder آجل دين مستحق تحصيل تذكير',
    titleEn: 'Selling on credit and collecting', titleAr: 'البيع بالآجل والتحصيل',
    en: [
      'At the till press "Sell on account" and pick the customer by name, code or phone, or make a sales invoice. A credit limit on the customer warns about, or stops, sales beyond it, as set in the sales settings.',
      'Receipts & cheques records what they pay. The customer page shows what they owe and a statement you can print or send.',
      'With the AI assistant on, it can write a polite WhatsApp payment reminder for you.'
    ],
    ar: [
      'في نقطة البيع اضغط "بيع بالآجل" واختر العميل بالاسم أو الرمز أو الجوال، أو أنشئ فاتورة مبيعات. حد الائتمان للعميل ينبّه أو يمنع البيع فوقه حسب إعدادات المبيعات.',
      'سجّل ما يدفعه من المقبوضات والشيكات. صفحة العميل تعرض المستحق عليه وكشف حساب يمكنك طباعته أو إرساله.',
      'عند تفعيل المساعد الذكي يمكنه كتابة رسالة تذكير بالدفع مهذبة عبر واتساب.'
    ]
  },
  {
    id: 'ai', icon: 'auto_awesome', keywords: 'ai assistant ask question summary whatsapp مساعد ذكي سؤال ملخص',
    titleEn: 'The AI assistant', titleAr: 'المساعد الذكي',
    en: [
      'Ask AI answers questions about your business in Arabic or English, such as "who owes me the most?" or "what sold best this week?". It only reads your reports and never changes anything.',
      'Each evening it can write a short summary of the day for the dashboard and email.',
      'Names and ID numbers are hidden from shared AI services.'
    ],
    ar: [
      'اسأل المساعد الذكي يجيب عن أسئلة نشاطك بالعربية أو الإنجليزية، مثل "من أكثر عميل مديون؟" أو "ما الأكثر مبيعاً هذا الأسبوع؟". هو يقرأ تقاريرك فقط ولا يغيّر أي شيء.',
      'كل مساء يمكنه كتابة ملخص قصير لليوم في لوحة المعلومات وبالبريد.',
      'تُخفى الأسماء وأرقام الهويات عن خدمات الذكاء الاصطناعي المشتركة.'
    ]
  },
  {
    id: 'support', icon: 'support_agent', keywords: 'support help contact problem suggestion دعم مساعدة تواصل مشكلة اقتراح',
    titleEn: 'Getting help from our support team', titleAr: 'طلب المساعدة من فريق الدعم',
    en: [
      'Press the headset button at the top → New request. Choose Problem, Question, Suggestion or New requirement, describe it, and attach a screenshot.',
      'The page you were on is sent with it. Our answer appears under My requests and by email; a red badge shows new answers.'
    ],
    ar: [
      'اضغط زر السماعة في الأعلى ← طلب جديد. اختر مشكلة أو سؤال أو اقتراح أو متطلب جديد، واشرح، وأرفق صورة من الشاشة.',
      'تُرسل الصفحة التي كنت فيها مع الطلب. يظهر ردنا في طلباتي وبالبريد، وتدل الشارة الحمراء على وجود رد جديد.'
    ]
  }
];

/** Articles matching the words typed (titles, keywords and text, either language), best first. */
export function searchHelp(text: string, limit = 3): HelpArticle[] {
  const words = text.toLowerCase().split(/[\s,.؟?!،]+/).filter((w) => w.length >= 3);
  if (!words.length) return [];
  return HELP_ARTICLES.map((a) => {
    const title = `${a.titleEn} ${a.titleAr}`.toLowerCase();
    const keys = a.keywords.toLowerCase();
    const body = `${a.en.join(' ')} ${a.ar.join(' ')}`.toLowerCase();
    const score = words.reduce((s, w) => s + (title.includes(w) ? 3 : 0) + (keys.includes(w) ? 2 : 0) + (body.includes(w) ? 1 : 0), 0);
    return { a, score };
  })
    .filter((x) => x.score >= 2)
    .sort((x, y) => y.score - x.score)
    .slice(0, limit)
    .map((x) => x.a);
}
