"""Freeze fresh semantic domains before helper model tuning. Metadata only.

All previously inspected cases become development data. New validation pools
are explicitly authored, with a correct title, close distractors and negatives.
No personal document contents are read. Output contains private source metadata.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path, PureWindowsPath


DOMAINS = [
    ('warranty', 'Appliance_warranty.pdf', ['Purchase_invoice.pdf', 'Product_manual.pdf', 'Repair_estimate.pdf', 'Return_policy.pdf'],
     ['pdf explaining the manufacturer coverage if the appliance breaks', 'pdf setting out the warranty period and covered repairs'],
     ['pdf listing the appliance electrical specifications', 'pdf proving the appliance passed a safety inspection']),
    ('inspection', 'Vehicle_inspection_report.pdf', ['Car_insurance.pdf', 'Vehicle_registration.pdf', 'Service_invoice.pdf', 'Driving_license.pdf'],
     ['pdf showing the results of the car roadworthiness check', 'pdf recording defects found during the vehicle inspection'],
     ['pdf recording my driving test result', 'pdf of the parking fine I received']),
    ('authorization', 'Power_of_attorney.pdf', ['Last_will.pdf', 'Identity_card.pdf', 'Employment_contract.pdf', 'Property_deed.pdf'],
     ['pdf authorizing someone to act legally on my behalf', 'pdf granting a representative legal authority to sign for me'],
     ['pdf granting permission for construction on my land', 'pdf confirming that my divorce was finalized']),
    ('paymentdetails', 'Supplier_bank_details.pdf', ['Supplier_invoice.pdf', 'Purchase_order.pdf', 'Delivery_note.pdf', 'Supplier_contract.pdf'],
     ['pdf with the account number for paying our supplier', 'pdf listing the supplier bank and transfer routing details'],
     ['pdf confirming the supplier received our transfer', 'pdf with the balance of our own bank account']),
    ('transcript', 'Academic_transcript.pdf', ['Graduation_certificate.pdf', 'Course_catalog.pdf', 'Class_timetable.pdf', 'Tuition_invoice.pdf'],
     ['pdf listing my courses and the grade for each one', 'pdf showing the marks I earned during my studies'],
     ['pdf of my university admission offer', 'pdf confirming my scholarship award']),
    ('reservation', 'Hotel_booking_confirmation.pdf', ['Hotel_brochure.pdf', 'Flight_itinerary.pdf', 'Restaurant_reservation.pdf', 'Travel_insurance.pdf'],
     ['pdf confirming the room I reserved for my stay', 'pdf with confirmation of my overnight accommodation booking'],
     ['pdf confirming my rental car pickup', 'pdf with the boarding gate for my flight']),
    ('nutrition', 'Weekly_meal_plan.xlsx', ['Grocery_prices.xlsx', 'Exercise_log.xlsx', 'Calorie_log.xlsx', 'Restaurant_expenses.xlsx'],
     ['spreadsheet planning what to eat for each day of the week', 'spreadsheet assigning breakfast lunch and dinner to each day'],
     ['spreadsheet listing medication doses for each day', 'spreadsheet of my blood test measurements']),
    ('maintenance', 'Equipment_maintenance_schedule.xlsx', ['Equipment_inventory.xlsx', 'Repair_costs.xlsx', 'Staff_roster.xlsx', 'Production_targets.xlsx'],
     ['spreadsheet showing when each machine is due for servicing', 'spreadsheet planning periodic equipment upkeep'],
     ['spreadsheet recording machine downtime that already occurred', 'spreadsheet of supplier delivery dates']),
    ('decisions', 'Architecture_decision_log.md', ['System_architecture.md', 'API_reference.md', 'Release_notes.md', 'Feature_requests.md'],
     ['markdown recording the technical choices we made and their reasons', 'markdown explaining why we chose particular architecture options'],
     ['markdown recording the outcome of our penetration test', 'markdown containing the team hiring decisions']),
    ('incident', 'Production_incident_postmortem.md', ['Monitoring_setup.md', 'Disaster_recovery_plan.md', 'Release_checklist.md', 'Performance_benchmark.md'],
     ['markdown explaining what caused the outage and what we learned', 'markdown analyzing the service disruption after it happened'],
     ['markdown with instructions for creating a monitoring dashboard', 'markdown listing staff emergency contact numbers']),
    ('localization', 'Localization_translation_catalog.json', ['Locale_settings.json', 'Currency_rates.json', 'User_preferences.json', 'Language_usage_stats.json'],
     ['json containing translated interface strings for multiple languages', 'json mapping interface messages to their translations'],
     ['json with audio transcripts of customer calls', 'json listing which languages each employee speaks']),
    ('permissions', 'Role_permission_matrix.csv', ['Employee_directory.csv', 'Login_history.csv', 'Department_list.csv', 'Feature_usage.csv'],
     ['csv mapping each user role to the actions it is allowed to perform', 'csv showing access rights assigned to different roles'],
     ['csv listing the passwords of all user accounts', 'csv recording failed authorization requests']),
    ('experiments', 'A_B_test_results.pptx', ['Product_launch.pptx', 'Market_research.pptx', 'Design_proposal.pptx', 'Sales_review.pptx'],
     ['slides comparing the measured outcomes of two tested variants', 'presentation showing which experiment variation performed better'],
     ['presentation showing the results of an employee satisfaction survey', 'presentation with our annual hiring forecast']),
    ('competitors', 'Competitor_comparison.pptx', ['Customer_personas.pptx', 'Brand_guidelines.pptx', 'Revenue_forecast.pptx', 'Product_training.pptx'],
     ['slides comparing our offering with rival products', 'presentation analyzing differences between competing vendors'],
     ['presentation comparing the salaries of our employees', 'slides showing the defects found in a code review']),
    ('handover', 'Employee_exit_checklist.docx', ['Employee_onboarding.docx', 'Equipment_inventory.docx', 'Performance_review.docx', 'Leave_request.docx'],
     ['document listing tasks to complete when an employee leaves the company', 'document covering account closure and asset return for departing staff'],
     ['document requesting reimbursement for moving expenses', 'document approving an employee promotion']),
    ('compliance', 'Data_retention_policy.docx', ['Privacy_notice.docx', 'Backup_procedure.docx', 'Access_request_form.docx', 'Incident_response_plan.docx'],
     ['document specifying how long records are kept before deletion', 'document stating retention periods for different kinds of stored data'],
     ['document listing which records were deleted during an audit', 'document certifying compliance with a particular regulation']),
]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    if args.output.exists():
        raise SystemExit('Refusing to overwrite frozen cases.')
    data = json.loads(args.source.read_text(encoding='utf-8'))
    for case in data['cases']:
        case['split'] = 'development'
    root = r'C:\ScoutHelperFixtures'
    data['roots'].append(root)
    for domain, title, decoys, positive, negative in DOMAINS:
        folder = str(PureWindowsPath(root) / domain)
        names = [title, *decoys]
        for index, name in enumerate(names):
            data['candidates'].append(dict(Id=f'helper:{domain}:{index}', Kind=0, Title=name,
                Target=str(PureWindowsPath(folder) / name), Detail=folder, Source='Authored helper fixture',
                CreatedAt=data['now'], ModifiedAt=data['now']))
        for index, query in enumerate([*positive, *negative]):
            expected = [f'helper:{domain}:0'] if index < len(positive) else []
            data['cases'].append(dict(id=f'helper-{domain}-{index}', query=f'{query} in "{folder}"',
                family='fresh-positive' if expected else 'fresh-negative', split='validation',
                expected=expected, order=[expected] if expected else [], mode='one' if expected else 'empty',
                count=None, folder=folder, direct=False, extensions=[PureWindowsPath(title).suffix],
                basis=None, action=0, literal=False, constraint=True))
    data['provenance'] = dict(sourceHash=hashlib.sha256(args.source.read_bytes()).hexdigest(),
        labels='New pools are authored filename-only challenges. No document contents read.',
        split='Previous 90 inspected scenarios are development; 16 fresh domains reserved for validation.')
    data['summary'] = dict(Counter(c['split'] for c in data['cases']))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2), encoding='utf-8')
    print(data['summary'])
    print('SHA256:', hashlib.sha256(args.output.read_bytes()).hexdigest())


if __name__ == '__main__':
    main()
