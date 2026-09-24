"""Freeze metadata-grounded descriptions and authored confusable-title challenges.

Reads only an existing frozen metadata corpus. No personal documents are opened.
Outputs contain private paths. Assess through Scout.Tests --search-eval, keeping
validation predictions sealed until the implementation is selected on development.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path, PureWindowsPath as WinPath
import re


# The first four domains were already inspected in the preceding diagnostic.
# Entire new domains, not just alternative wording, are reserved for validation.
REAL = [
    ('receipt', 'development', r'receipt', '.pdf', ['pdf showing proof of payment', 'pdf recording what I paid for a purchase']),
    ('roadmap', 'development', r'roadmap', '.md', ['markdown describing future plans', 'markdown with the project direction']),
    ('schema', 'development', r'schema', '.json', ['json file defining the data structure', 'json describing the allowed fields and their types']),
    ('guide', 'development', r'guide|manual', '.pdf', ['pdf explaining how to use something', 'pdf with usage instructions']),
    ('architecture', 'validation', r'architecture', '.md', ['markdown describing how the system is structured', 'markdown explaining the components and how they fit together']),
    ('deployment', 'validation', r'deployment', '.md', ['markdown about putting the application into production', 'markdown explaining how to roll out the software']),
    ('backup', 'validation', r'backup', '.md', ['markdown explaining how to make a recovery copy of data', 'markdown about restoring data after it is lost']),
    ('security', 'validation', r'security', '.md', ['markdown about protecting the application against attacks', 'markdown describing defenses against unauthorized access']),
]

# Authored titles isolate near-neighbor distinctions; these are not claims about
# the user's documents. Each pool also gets a query unsupported by every title.
AUTHORED = [
    ('receipt', 'development', 'Purchase_receipt.pdf', ['invoice-no-trn.pdf', 'Payment_schedule.pdf', 'Refund_policy.pdf', 'Purchase_order.pdf'],
     ['pdf confirming I already paid for something', 'pdf acknowledging payment for a purchase'], 'pdf listing the balance in my savings account'),
    ('roadmap', 'development', '00-current-roadmap.md', ['01-platform-architecture.md', '07-deployment.md', 'Frontend_implementation.md', 'Requirements-checklist.md'],
     ['markdown laying out the planned product milestones', 'markdown showing where the project is headed'], 'markdown with the minutes of yesterday\'s team meeting'),
    ('schema', 'development', 'routing.schema.json', ['routing.config.json', 'sample-response.json', 'package.json', 'test-results.json'],
     ['json defining which fields and types are valid', 'json specifying the shape of the data'], 'json containing yesterday\'s weather measurements'),
    ('guide', 'development', 'Application_User_Guide.pdf', ['Application_Release_Notes.pdf', 'Application_Architecture.pdf', 'Application_Pricing.pdf', 'Engineering_Handover.pdf'],
     ['pdf teaching me how to use the application', 'pdf with step by step instructions for users'], 'pdf with my medical prescription'),
    ('diploma', 'development', 'University_Diploma.pdf', ['Course_timetable.pdf', 'University_invoice.pdf', 'Transcript.pdf', 'Student_handbook.pdf'],
     ['pdf proof that I finished university', 'pdf certifying my university degree'], 'pdf showing my vaccination history'),
    ('employment', 'development', 'Employment_agreement.pdf', ['Job_description.pdf', 'Curriculum_vitae.pdf', 'Interview_questions.pdf', 'Employee_handbook.pdf'],
     ['pdf with the terms of my job', 'pdf setting out the contract with my employer'], 'pdf with the lease for my apartment'),
    ('expenses', 'development', 'Travel_expenses.xlsx', ['Travel_budget.xlsx', 'Flight_schedule.xlsx', 'Sales_forecast.xlsx', 'Inventory.xlsx'],
     ['spreadsheet tracking money spent on trips', 'spreadsheet recording the costs of travel'], 'spreadsheet with students\' exam grades'),
    ('meeting', 'development', 'Team_meeting_minutes.docx', ['Team_agenda.docx', 'Meeting_invitation.docx', 'Project_proposal.docx', 'Interview_preparation.docx'],
     ['document with notes from the team discussion', 'document recording what was discussed at the meeting'], 'document with the ingredients for a chocolate cake'),
    ('shipping', 'validation', 'Shipment_tracking.pdf', ['Purchase_receipt.pdf', 'Shipping_rates.pdf', 'Warehouse_inventory.pdf', 'Return_policy.pdf'],
     ['pdf showing where my parcel is on its journey', 'pdf for following the progress of a delivery'], 'pdf with my boarding pass for a flight'),
    ('passport', 'validation', 'Passport_scan.pdf', ['Flight_itinerary.pdf', 'Visa_application.pdf', 'Travel_insurance.pdf', 'Hotel_booking.pdf'],
     ['pdf copy of my international travel identity document', 'pdf scan of the document proving my nationality for travel'], 'pdf of my driving license'),
    ('vaccination', 'validation', 'Vaccination_record.pdf', ['Medical_prescription.pdf', 'Lab_results.pdf', 'Health_insurance.pdf', 'Doctor_appointment.pdf'],
     ['pdf showing which immunizations I have received', 'pdf with my history of vaccine doses'], 'pdf with the results of my eye exam'),
    ('pay', 'validation', 'Monthly_payslip.pdf', ['Employment_agreement.pdf', 'Tax_return.pdf', 'Expense_reimbursement.pdf', 'Salary_offer.pdf'],
     ['pdf breaking down my salary and deductions this month', 'pdf showing this month\'s take home pay'], 'pdf showing dividends from my investments'),
    ('recipe', 'validation', 'Chocolate_cake_recipe.docx', ['Restaurant_menu.docx', 'Grocery_list.docx', 'Nutrition_guidelines.docx', 'Catering_proposal.docx'],
     ['document with instructions for baking a chocolate cake', 'document listing ingredients and steps to make chocolate cake'], 'document explaining how to repair an oven'),
    ('contacts', 'validation', 'Employee_contact_directory.xlsx', ['Employee_attendance.xlsx', 'Payroll.xlsx', 'Office_inventory.xlsx', 'Recruitment_pipeline.xlsx'],
     ['spreadsheet with colleagues\' phone numbers and email addresses', 'spreadsheet listing how to reach each employee'], 'spreadsheet recording employee performance ratings'),
    ('accessibility', 'validation', 'Accessibility_audit.md', ['Security_audit.md', 'Performance_report.md', 'Design_system.md', 'Release_checklist.md'],
     ['markdown evaluating whether people with disabilities can use the app', 'markdown checking support for screen readers and keyboard navigation'], 'markdown explaining our database disaster recovery procedure'),
    ('migration', 'validation', 'Database_migration_plan.md', ['Database_schema.md', 'Database_backup.md', 'API_reference.md', 'Server_deployment.md'],
     ['markdown planning how to move data to a new database', 'markdown with the steps for switching database systems'], 'markdown with our employee holiday policy'),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite a frozen corpus.')
    data = json.loads(args.source.read_text(encoding='utf-8'))
    files = data['candidates']
    cases = []

    def add(query, family, split, folder, ext, expected):
        cases.append(dict(id=f'semantic-{len(cases):03d}', query=f'{query} in "{folder}"', family=family, split=split,
                          expected=expected, order=[expected] if expected else [], mode='one' if expected else 'empty',
                          count=None, folder=folder, direct=False, extensions=[ext], basis=None,
                          action=0, literal=False, constraint=True))

    for domain, split, pattern, ext, queries in REAL:
        matches = [c for c in files if WinPath(c['Target']).suffix.lower() == ext and re.search(pattern, c['Title'], re.I)]
        folders = sorted({str(WinPath(c['Target']).parent) for c in matches}, key=lambda p: hashlib.sha256(p.lower().encode()).hexdigest())[:3]
        for folder in folders:
            expected = [c['Id'] for c in matches if c['Target'].lower().startswith(folder.lower() + '\\')]
            for query in queries:
                add(query, 'real-' + domain, split, folder, ext, expected)

    synthetic_root = r'C:\ScoutSemanticFixtures'
    synthetic = []
    for domain, split, title, decoys, queries, negative in AUTHORED:
        folder = str(WinPath(synthetic_root) / domain)
        for i, name in enumerate([title, *decoys]):
            synthetic.append(dict(Id=f'authored:{domain}:{i}', Kind=0, Title=name, Target=str(WinPath(folder) / name), Detail=folder, Source='Authored semantic fixture'))
        ext = WinPath(title).suffix.lower()
        for query in queries:
            add(query, 'authored-positive', split, folder, ext, [f'authored:{domain}:0'])
        add(negative, 'authored-negative', split, folder, ext, [])

    report = dict(now=data['now'], roots=[*data['roots'], synthetic_root], candidates=[*files, *synthetic], cases=cases,
                  provenance=dict(sourceHash=hashlib.sha256(args.source.read_bytes()).hexdigest(),
                                  labels='Real labels inferred from titles only; authored pools explicitly synthetic. No content read.',
                                  split='Reserved entire new domains before model experiments.'),
                  summary=dict(cases=len(cases), splits=dict(Counter(c['split'] for c in cases)), families=dict(Counter(c['family'] for c in cases))))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report['summary'], indent=2))
    print('SHA256:', hashlib.sha256(args.output.read_bytes()).hexdigest())


if __name__ == '__main__':
    main()
