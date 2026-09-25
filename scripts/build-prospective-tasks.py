"""Add fresh authored filename-only decision pools to an existing Scout snapshot.

Run once, then freeze with freeze-task-benchmark.py before model inspection.
The output contains private metadata copied from the base snapshot and belongs
under ignored artifacts/diagnostics. No file contents are read.
"""

import argparse
from collections import Counter
import json
from pathlib import Path, PureWindowsPath


# Each entire domain stays in one split. Positives require a relation visible in
# the target title; negatives share a topic with the pool but ask for a document
# that is not present. These are authored tasks, not observed user searches.
DOMAINS = [
    ("tenancy", "Lease_renewal_offer.pdf", ["Lease_application.pdf", "Rent_receipt.pdf", "Move_in_inventory.pdf", "Property_inspection.pdf"],
     ["pdf offering terms to continue renting my apartment", "pdf proposing another term on my rental lease"],
     ["pdf denying my lease renewal", "pdf calculating a penalty for ending my lease early"]),
    ("refund", "Refund_approval_email.pdf", ["Return_shipping_label.pdf", "Payment_receipt.pdf", "Refund_request.pdf", "Store_credit_policy.pdf"],
     ["pdf saying my request to get the payment back was approved", "pdf confirming the store agreed to refund me"],
     ["pdf refusing my refund request", "pdf showing the exact day the refund reached my bank"]),
    ("customs", "Import_customs_declaration.pdf", ["Shipping_manifest.pdf", "Commercial_invoice.pdf", "Import_license.pdf", "Duty_payment_receipt.pdf"],
     ["pdf declaring goods brought across the border", "pdf listing my shipment for customs entry"],
     ["pdf announcing a customs seizure", "pdf granting a waiver of the import duty"]),
    ("grant", "Grant_award_letter.pdf", ["Grant_application.pdf", "Budget_proposal.pdf", "Eligibility_guide.pdf", "Payment_schedule.pdf"],
     ["pdf confirming our funding application succeeded", "pdf saying the grant was awarded to our project"],
     ["pdf rejecting our grant application", "pdf reporting a grant compliance audit finding"]),
    ("calibration", "Sensor_calibration_certificate.pdf", ["Sensor_manual.pdf", "Maintenance_log.pdf", "Sensor_readings.pdf", "Calibration_schedule.pdf"],
     ["pdf certifying the sensor was checked for measurement accuracy", "pdf proving the measuring device passed calibration"],
     ["pdf declaring the sensor failed calibration", "pdf requesting replacement of the faulty sensor"]),
    ("overtime", "Overtime_approval_form.xlsx", ["Shift_roster.xlsx", "Timesheet.xlsx", "Payslip.xlsx", "Leave_balance.xlsx"],
     ["spreadsheet authorizing extra hours worked", "spreadsheet approving the team's overtime"],
     ["spreadsheet rejecting an overtime request", "spreadsheet explaining deductions from overtime pay"]),
    ("consent", "Consent_withdrawal_request.docx", ["Consent_form.docx", "Privacy_notice.docx", "Data_export_request.docx", "Account_closure.docx"],
     ["document asking to revoke permission to use my data", "document withdrawing consent for processing my information"],
     ["document confirming the company erased my data", "document notifying me of a privacy breach"]),
    ("hazards", "Workplace_hazard_assessment.docx", ["Safety_training.docx", "Incident_report.docx", "Evacuation_plan.docx", "Equipment_checklist.docx"],
     ["document evaluating risks before work starts", "document identifying workplace hazards and their controls"],
     ["document recording the outcome of a fire drill", "document closing a workplace injury claim"]),
    ("trip", "Trip_cancellation_confirmation.pdf", ["Trip_booking.pdf", "Travel_insurance.pdf", "Boarding_pass.pdf", "Flight_change_notice.pdf"],
     ["pdf confirming that my travel reservation was cancelled", "pdf acknowledging cancellation of my trip"],
     ["pdf promising compensation for a delayed flight", "pdf confirming my travel refund has been paid"]),
    ("recall", "Product_recall_notice.pdf", ["Quality_inspection.pdf", "Product_manual.pdf", "Customer_support.pdf", "Return_label.pdf"],
     ["pdf warning customers to stop using a defective product", "pdf announcing the manufacturer is recalling an unsafe item"],
     ["pdf certifying a product recall is complete", "pdf offering an extended product warranty"]),
    ("patent", "Patent_assignment_agreement.pdf", ["Invention_disclosure.pdf", "Patent_application.pdf", "Employee_contract.pdf", "License_agreement.pdf"],
     ["pdf transferring ownership of an invention to another party", "pdf assigning the rights to a patent"],
     ["pdf rejecting the patent application", "pdf confirming the patent filing fee was paid"]),
    ("allergy", "Medication_allergy_action_plan.pdf", ["Prescription.pdf", "Allergy_test_results.pdf", "Clinic_visit.pdf", "Insurance_claim.pdf"],
     ["pdf explaining what to do if I have an allergic reaction to medicine", "pdf with emergency steps for my drug allergy"],
     ["pdf listing all side effects of my new medicine", "pdf confirming my allergy treatment was reimbursed"]),
    ("restore", "Backup_restore_verification.log", ["Backup_schedule.log", "Backup_error.log", "Restore_instructions.log", "Storage_report.log"],
     ["log proving a backup was successfully restored in a test", "log verifying the recovery procedure worked"],
     ["log recording a failed encryption key rotation", "log confirming an offsite backup was destroyed"]),
    ("dictionary", "Data_dictionary.csv", ["Sales_data.csv", "Customer_export.csv", "Database_schema.csv", "Metric_catalog.csv"],
     ["csv defining what each dataset field means", "csv describing the columns and values in our data"],
     ["csv tracking requests to delete customer data", "csv recording who accessed a database table yesterday"]),
    ("a11y", "Accessibility_test_results.pdf", ["Design_system.pdf", "User_interview.pdf", "Browser_compatibility.pdf", "Release_notes.pdf"],
     ["pdf with findings from screen reader testing", "pdf reporting whether keyboard users can complete the interface"],
     ["pdf certifying the website meets every accessibility rule", "pdf granting an accessibility exemption"]),
    ("reimbursement", "Expense_reimbursement_approval.pdf", ["Expense_claim.pdf", "Travel_receipt.pdf", "Payment_schedule.pdf", "Budget_report.pdf"],
     ["pdf approving repayment of an employee's expenses", "pdf agreeing to reimburse the submitted costs"],
     ["pdf rejecting the employee expense claim", "pdf confirming the reimbursement reached the employee's account"]),
    ("speaker", "Conference_speaker_contract.pdf", ["Session_agenda.pdf", "Conference_ticket.pdf", "Speaker_bio.pdf", "Venue_invoice.pdf"],
     ["pdf setting out the agreement with a conference presenter", "pdf containing the speaker's contractual terms"],
     ["pdf cancelling the conference speaker's appearance", "pdf inviting me to speak at next year's event"]),
    ("vendorsecurity", "Vendor_security_questionnaire.xlsx", ["Supplier_contract.xlsx", "Risk_register.xlsx", "Audit_report.xlsx", "Vendor_contacts.xlsx"],
     ["spreadsheet answering questions about a supplier's security controls", "spreadsheet assessing the vendor's data protection practices"],
     ["spreadsheet notifying us that the vendor suffered a breach", "spreadsheet approving an exception to the supplier security policy"]),
    ("namechange", "Name_change_certificate.pdf", ["Passport_scan.pdf", "Birth_certificate.pdf", "Marriage_license.pdf", "Address_proof.pdf"],
     ["pdf providing official proof that my legal name changed", "pdf certifying my change of name"],
     ["pdf confirming my passport renewal appointment", "pdf documenting when I moved to my new address"]),
    ("preregistration", "Experiment_preregistration.md", ["Experiment_results.md", "Research_notes.md", "Dataset_card.md", "Analysis_plan.md"],
     ["markdown recording the study hypotheses before results were known", "markdown registering the experiment plan ahead of data collection"],
     ["markdown presenting the final conclusions of the experiment", "markdown confirming every study participant gave consent"]),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, required=True, help="Existing frozen Scout dataset")
    parser.add_argument("--source-out", type=Path, required=True)
    parser.add_argument("--tasks-out", type=Path, required=True)
    args = parser.parse_args()
    if args.source_out.exists() or args.tasks_out.exists():
        parser.error("Refusing to overwrite an existing source or task file")
    source = json.loads(args.base.read_text(encoding="utf-8"))
    source = {"now": source["now"], "roots": list(source["roots"]), "candidates": list(source["candidates"])}
    root = PureWindowsPath(r"C:\ScoutProspectiveFixtures")
    source["roots"].append(str(root))
    cases = []
    for domain_index, (domain, title, decoys, positives, negatives) in enumerate(DOMAINS):
        folder = root / domain
        for item_index, name in enumerate([title, *decoys]):
            source["candidates"].append({"Id": f"prospective:{domain}:{item_index}", "Kind": 0,
                                          "Title": name, "Target": str(folder / name), "Detail": str(folder),
                                          "Source": "Authored prospective task", "CreatedAt": source["now"],
                                          "ModifiedAt": source["now"]})
        split = "development" if domain_index < len(DOMAINS) // 2 else "holdout"
        for task_index, query in enumerate([*positives, *negatives]):
            positive = task_index < len(positives)
            cases.append({"id": f"prospective-{domain}-{task_index}", "query": f'{query} in "{folder}"',
                          "expected": f"prospective:{domain}:0" if positive else [],
                          "family": "description-positive" if positive else "close-no-match",
                          "split": split, "folder": str(folder), "extensions": [PureWindowsPath(title).suffix.lower()]})
    args.source_out.parent.mkdir(parents=True, exist_ok=True)
    args.tasks_out.parent.mkdir(parents=True, exist_ok=True)
    args.source_out.write_text(json.dumps(source, ensure_ascii=False), encoding="utf-8")
    args.tasks_out.write_text(json.dumps({"cases": cases}, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"domains": len(DOMAINS), "candidates": len(source["candidates"]), "cases": len(cases),
                      "splits": dict(Counter(c["split"] for c in cases))}, indent=2))


if __name__ == "__main__":
    main()
