/** Starting points for the quick-setup wizard: which modules each kind of business uses and a starter price list. */

export interface TemplateItem {
  sku: string;
  en: string;
  ar: string;
  type: 'Service' | 'Stock';
  price: number;
  cost?: number;
}

export interface BusinessTemplate {
  type: string;
  icon: string;
  modules: string[];
  till: boolean;
  salonStaff?: boolean;
  items: TemplateItem[];
}

export const TEMPLATES: BusinessTemplate[] = [
  {
    type: 'tailoring', icon: 'checkroom', till: true, modules: ['hr', 'ess', 'inventory', 'sales', 'pos', 'tailoring'],
    items: [
      { sku: 'STITCH-ABY', en: 'Abaya stitching', ar: 'خياطة عباية', type: 'Service', price: 250 },
      { sku: 'STITCH-DRS', en: 'Dress stitching', ar: 'خياطة فستان', type: 'Service', price: 350 },
      { sku: 'STITCH-JLB', en: 'Jalabiya stitching', ar: 'خياطة جلابية', type: 'Service', price: 180 },
      { sku: 'ALTER', en: 'Alteration', ar: 'تعديل', type: 'Service', price: 40 },
      { sku: 'EMBROID', en: 'Embroidery / stones', ar: 'تطريز / أحجار', type: 'Service', price: 100 },
      { sku: 'CREPE', en: 'Black crepe (m)', ar: 'كريب أسود (متر)', type: 'Stock', price: 30, cost: 14 },
      { sku: 'NIDA', en: 'Nida fabric (m)', ar: 'قماش ندى (متر)', type: 'Stock', price: 38, cost: 18 },
      { sku: 'SHAYLA', en: 'Shayla (scarf)', ar: 'شيلة', type: 'Stock', price: 45, cost: 15 }
    ]
  },
  {
    type: 'salon', icon: 'content_cut', till: false, salonStaff: true, modules: ['hr', 'ess', 'inventory', 'sales', 'salon'],
    items: [
      { sku: 'CUT', en: 'Haircut & style', ar: 'قص وتسريح', type: 'Service', price: 80 },
      { sku: 'BLOW', en: 'Blow-dry', ar: 'سشوار', type: 'Service', price: 60 },
      { sku: 'COLOR', en: 'Hair colour', ar: 'صبغة شعر', type: 'Service', price: 250 },
      { sku: 'HIGH', en: 'Highlights', ar: 'هايلايت', type: 'Service', price: 350 },
      { sku: 'MANI', en: 'Manicure', ar: 'مناكير', type: 'Service', price: 50 },
      { sku: 'PEDI', en: 'Pedicure', ar: 'باديكير', type: 'Service', price: 70 },
      { sku: 'THREAD', en: 'Threading', ar: 'خيط', type: 'Service', price: 20 },
      { sku: 'HENNA', en: 'Henna', ar: 'حناء', type: 'Service', price: 100 },
      { sku: 'BEARD', en: 'Beard trim', ar: 'تشذيب اللحية', type: 'Service', price: 30 },
      { sku: 'BRIDE', en: 'Bridal make-up', ar: 'مكياج عروس', type: 'Service', price: 900 }
    ]
  },
  {
    type: 'grocery', icon: 'local_grocery_store', till: true, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'pos'],
    items: [
      { sku: 'RICE5', en: 'Basmati rice 5 kg', ar: 'أرز بسمتي 5 كغ', type: 'Stock', price: 45, cost: 32 },
      { sku: 'SUGAR2', en: 'Sugar 2 kg', ar: 'سكر 2 كغ', type: 'Stock', price: 8.5, cost: 6 },
      { sku: 'OIL18', en: 'Cooking oil 1.8 L', ar: 'زيت طبخ 1.8 لتر', type: 'Stock', price: 13, cost: 9.5 },
      { sku: 'WATER6', en: 'Water 1.5 L × 6', ar: 'ماء 1.5 لتر × 6', type: 'Stock', price: 9, cost: 6 },
      { sku: 'MILK2', en: 'Fresh milk 2 L', ar: 'حليب طازج 2 لتر', type: 'Stock', price: 10.5, cost: 7.5 },
      { sku: 'EGGS30', en: 'Eggs (tray of 30)', ar: 'بيض (طبق 30)', type: 'Stock', price: 19, cost: 14 },
      { sku: 'BREAD', en: 'Arabic bread', ar: 'خبز عربي', type: 'Stock', price: 2, cost: 1.2 },
      { sku: 'DELIVER', en: 'Home delivery', ar: 'توصيل للمنزل', type: 'Service', price: 5 }
    ]
  },
  {
    type: 'retail', icon: 'storefront', till: true, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'pos'],
    items: [
      { sku: 'ITEM-A', en: 'Product A', ar: 'منتج أ', type: 'Stock', price: 50, cost: 30 },
      { sku: 'ITEM-B', en: 'Product B', ar: 'منتج ب', type: 'Stock', price: 120, cost: 75 },
      { sku: 'GIFT', en: 'Gift wrapping', ar: 'تغليف هدايا', type: 'Service', price: 10 }
    ]
  },
  {
    type: 'restaurant', icon: 'restaurant', till: true, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'pos'],
    items: [
      { sku: 'KARAK', en: 'Karak tea', ar: 'شاي كرك', type: 'Service', price: 2 },
      { sku: 'SHAW', en: 'Chicken shawarma', ar: 'شاورما دجاج', type: 'Service', price: 8 },
      { sku: 'BIRY', en: 'Chicken biryani', ar: 'برياني دجاج', type: 'Service', price: 22 },
      { sku: 'MANDI', en: 'Lamb mandi', ar: 'مندي لحم', type: 'Service', price: 45 },
      { sku: 'JUICE', en: 'Fresh juice', ar: 'عصير طازج', type: 'Service', price: 12 },
      { sku: 'FALAFEL', en: 'Falafel sandwich', ar: 'ساندويتش فلافل', type: 'Service', price: 5 }
    ]
  },
  {
    type: 'furniture', icon: 'chair', till: false, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'crm'],
    items: [
      { sku: 'SOFA3', en: '3-seater sofa', ar: 'كنبة 3 مقاعد', type: 'Stock', price: 2400, cost: 1300 },
      { sku: 'BED-K', en: 'King bed', ar: 'سرير كينج', type: 'Stock', price: 2900, cost: 1600 },
      { sku: 'DINING6', en: 'Dining set (6 chairs)', ar: 'طاولة طعام 6 كراسي', type: 'Stock', price: 3200, cost: 1800 },
      { sku: 'MAJLIS', en: 'Majlis set', ar: 'طقم مجلس', type: 'Stock', price: 7500, cost: 4200 },
      { sku: 'DELIVERY', en: 'Delivery', ar: 'توصيل', type: 'Service', price: 100 },
      { sku: 'INSTALL', en: 'Installation', ar: 'تركيب', type: 'Service', price: 150 }
    ]
  },
  {
    type: 'workshop', icon: 'car_repair', till: false, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales'],
    items: [
      { sku: 'L-OIL', en: 'Oil change labour', ar: 'أجرة تغيير زيت', type: 'Service', price: 40 },
      { sku: 'L-BRAKE', en: 'Brake job labour', ar: 'أجرة صيانة فرامل', type: 'Service', price: 120 },
      { sku: 'L-AC', en: 'AC service and gas', ar: 'صيانة المكيف والغاز', type: 'Service', price: 200 },
      { sku: 'L-DIAG', en: 'Computer diagnostics', ar: 'فحص بالكمبيوتر', type: 'Service', price: 100 },
      { sku: 'OIL5W30', en: 'Engine oil 5W-30 (4 L)', ar: 'زيت محرك 5W-30 (4 لتر)', type: 'Stock', price: 95, cost: 55 },
      { sku: 'OILFILTER', en: 'Oil filter', ar: 'فلتر زيت', type: 'Stock', price: 30, cost: 12 },
      { sku: 'BRAKEPADS', en: 'Brake pads (set)', ar: 'فحمات فرامل (طقم)', type: 'Stock', price: 160, cost: 70 }
    ]
  },
  {
    type: 'typing_office', icon: 'badge', till: false, modules: ['hr', 'ess', 'inventory', 'sales', 'crm'],
    items: [
      { sku: 'TYPING', en: 'Typing & form filling', ar: 'طباعة وتعبئة نماذج', type: 'Service', price: 30 },
      { sku: 'VISA', en: 'Work visa application', ar: 'طلب تأشيرة عمل', type: 'Service', price: 150 },
      { sku: 'QID', en: 'QID renewal service', ar: 'خدمة تجديد البطاقة الشخصية', type: 'Service', price: 75 },
      { sku: 'WORKPERMIT', en: 'Work permit renewal', ar: 'تجديد إذن العمل', type: 'Service', price: 150 },
      { sku: 'TRANSLATE', en: 'Translation (per page)', ar: 'ترجمة (للصفحة)', type: 'Service', price: 50 },
      { sku: 'ATTEST', en: 'Document attestation', ar: 'تصديق مستندات', type: 'Service', price: 100 },
      { sku: 'CR-FOLLOW', en: 'CR renewal follow-up', ar: 'متابعة تجديد السجل التجاري', type: 'Service', price: 300 }
    ]
  },
  {
    type: 'contracting', icon: 'engineering', till: false, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'projects', 'subcontracts', 'assets'],
    items: [
      { sku: 'LABOUR-DAY', en: 'Labourer (per day)', ar: 'عامل (لليوم)', type: 'Service', price: 120 },
      { sku: 'MASON-DAY', en: 'Mason (per day)', ar: 'بنّاء (لليوم)', type: 'Service', price: 200 },
      { sku: 'EQUIP-HOUR', en: 'Equipment hire (per hour)', ar: 'تأجير معدات (للساعة)', type: 'Service', price: 250 }
    ]
  },
  {
    type: 'trading', icon: 'local_shipping', till: false, modules: ['hr', 'ess', 'inventory', 'purchasing', 'sales', 'crm', 'budgets'],
    items: [
      { sku: 'DELIVERY', en: 'Delivery', ar: 'توصيل', type: 'Service', price: 150 }
    ]
  }
];
