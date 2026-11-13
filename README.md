# kPOS
Independent liquor store POS system

## kPOS – Product / Development Report

November 13th 2026

Windows oriented Point-Of-Sale and back office software for independent liquor retail
Current Stage: Windows testing preview, v0.1.0-preview.9

## Executive Summary
kPOS is being built to replace an aging LiquorPOS system that the store uses to ring up sales, manage products, track inventory, place orders, and close the day. The current Windows XP based system contains years of work that would be expensive to recreate and easy to lose in a haphazard transition. The mission statement is to preserve that progress while also modernizing the current system.

The project has moved from a web prototype to a native Windows application. The preview currently is able to scan, sell, record sales. Reconciling drawer and card terminal at closeout, as well as tracking stock from counts and deliveries are included as functionalities in the current preview.

Next investment focuses on migration of existing product data, daily operating controls, and recovery. Using existing product CSV files with all dataframes, we should be looking into record keeping and transitioning means. Once conditions for updating have been met, the next step is supervised piloting to make sure that the software is fully integrated into actual drawer and database hardware.

## kPOS Required Functionality
Cashier	> 	Start with scanner ready, ring up sales by keyboard, handle exceptions 
and various test cases, be visually simplistic, and complete sales with 
clear recordkeeping of payment, change, sales data

Management	>	Maintain products and prices, verify stock, review sales trends, prepare 
distributor orders, receive deliveries, investigate discrepancies

Bookkeeping	>	Reconcile the day, retrieve sales/price history, export records, control 
access to sensitive actions, and recover register if needed

## Roadmap
Sales/Checkout/Closeout bug mitigation and test case passes
Before launch, a verification of sales reliability and handling of unexpected errors. Validation of software compatibility with existing hardware.

Tax specifications
More options to freely edit and determine locational tax data. While the state rate is currently an inputtable feature, local taxes and product treatment needs to be checked. Lottery sales and mixed baskets must be validated before release.

Catalog
Product IDs, UPCs, barcodes, descriptions, categories, sizes, case packs, prices, tax flags, discounts, discontinued items, vendor item codes 

Inventory/Sales
Current counts, supplier records, last or average costs, order and receiving history, open orders, and recent invoices, or packing slips

Sales/Closeouts
Transaction headers and line items, returns, payment categories, tax, lottery, daily closeouts
May need a sample report for reference

Operating Setup
Register and hardware details, tax settings, drawer procedures, and examples of receipts and reports

Quick rundown of operational takeover:
Confirm the scope and collect data, observe and note normal shifts, a closeout, order, delivery cycle. Gather legacy export data, sample reports, and hardware details. Identify workflows and gather critical operational data.
Rehearse migration, build the import and map/validate fields, and load a copy of the catalog and available history. Compare results with legacy records and test with the full catalog.
Prepare for live testing/use case, complete cashier and manger controls, paid-sale corrections, receipts, and store/location specific tax, lottery, terminal checks. Verify hardware, backups, and recovery on a replacement PC.
Pilot and eventual take over, run side-by-side tests, train staff, schedule switch, provide direct support through the first actual live deployment.
Improve purchasing and evaluation, add persistent purchase orders, receiving against orders, stronger inventory controls, reports based on store purchases. Evaluation period may include multiple registers and integrations, and broader deployment depending on pilot results.

Important Variables:
> Sales accuracy

> Migration coverage

> Staff training

> Recovery

> Release quality

> Ease of use

## Relevant References
Colorado sales tax guide
https://tax.colorado.gov/sales-tax-guide

Colorado liquor rules
https://sbg.colorado.gov/sites/sbg/files/documents/2026_Colorado_Liquor_Rules_eff_01.2026.pdf

Colorado Lottery FAQs
https://www.coloradolottery.com/en/about/faqs/?lv=true

