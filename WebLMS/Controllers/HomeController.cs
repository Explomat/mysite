﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using VClass;
using System.Net.Mime;
using System.Threading;
using System.Diagnostics;
using WebLMS.Models;
using WebLMS.Utils;
using WebLMS.Utils.Sender;

namespace WebLMS.Controllers
{
    public class HomeController : Controller
    {
        private WebLMSContext _db = new WebLMSContext();

        //private void SendMessage(WebLMSForm form)
        //{
        //    MailMessage m = new MailMessage();
        //    SmtpClient sc = new SmtpClient();
        //    m.From = new MailAddress("cabehok@inbox.ru");
        //    m.To.Add("gabdsh@gmail.com");
        //    m.Subject = String.Format("Заявка от {0}", form.Fullname);
        //    m.Body = String.Format("ФИО - {0} \r\n Email - {1} \r\n Телефон - {2} \r\n Срочный заказ  - {3} \r\n Описание - {4}", form.Fullname, form.Email, form.Phone, form.IsQuickly, form.Description);
        //    sc.Host = "smtp.mail.ru";
        //    try
        //    {
        //        sc.Port = 25;
        //        sc.Credentials = new System.Net.NetworkCredential("cabehok@inbox.ru", "1qaz2wsx3edc4RFV");
        //        sc.EnableSsl = false;
        //        sc.Send(m);
        //    }
        //    catch (Exception ex)
        //    {
               
        //    }
        //}

        public ActionResult Index()
        {
            return View();
        }

        [HttpPost]
        public ActionResult CreateForm(WebLMSForm form)
        {
            form.RequestDate = DateTime.Now;
            _db.WebLMSForms.Add(form);
            _db.SaveChanges();
            return RedirectToAction("Index");
        }

        public ActionResult RequestList()
        {
            return View(_db.WebLMSForms.ToList());
        }

        [HttpGet]
        public void GetVideoFile(string hash, Int64 id)
        {
            if (hash == null)
            {
                Response.Write("Указаны неверные параметры!");
                return;
            }

            Models.File file = _db.Files.Where(f => f.Id == id && f.Md5Hash == hash).FirstOrDefault<Models.File>();
            if (file == null)
            {
                Response.Write("Указаны неверные параметры!");
                return;
            }
            file.IsDownloaded = true;
            _db.SaveChanges();

            string filePath = Server.MapPath(file.FilePath);
            string ext = Path.GetExtension(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            if (!System.IO.File.Exists(filePath))
            {
                Response.Write("Файл не найден, т.к. уже удален с сервера!");
                return;
            }
            try
            {
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    Response.BufferOutput = false;   // to prevent buffering
                    Response.ContentType = "video/avi";
                    Response.AddHeader("Content-Length", fs.Length.ToString());
                    Response.AddHeader("content-disposition", "attachment;filename=\"" + fileName + ext + "\"");
                    
                    byte[] buffer = new byte[4096];
                    int bytesRead = 0;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        Response.OutputStream.Write(buffer, 0, bytesRead);
                    }
                }
                System.IO.File.Delete(filePath);
            }

            catch (Exception ex) { Debug.WriteLine(ex); } 
        }

        [HttpPost]
        public ActionResult ConvertForm(HttpPostedFileBase fileUpload, string email = "")
        {
            if (fileUpload == null || fileUpload.ContentLength == 0)
            {
                return Json(new { error = "Не выбран или поврежден файл!" });
            }
            if (!Email.IsValidEmail(email))
            {
                return Json(new { error = "Неправильный email адрес!" });
            }
            using (MD5 md5 = MD5.Create())
            {
                try
                {
                    string hash = Hash.GetMD5Hash(md5, fileUpload.InputStream);
                    string destDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TempVideoFiles");
                    string destFullDirectory = Path.Combine(destDirectory, email, hash);
                    string videoDirectory = Path.Combine(destFullDirectory, "video");
                    if (!Directory.Exists(destDirectory))
                    {
                        Directory.CreateDirectory(destDirectory);
                    }
                    
                    if (!Directory.Exists(destFullDirectory))
                    {
                        Directory.CreateDirectory(destFullDirectory);
                        Directory.CreateDirectory(videoDirectory);
                        Zip.Unzip(fileUpload.InputStream, destFullDirectory);
                    }

                    VideoConverter converter = new VideoConverter(destFullDirectory, videoDirectory);
                    string error = converter.CheckArchiveCorrect();
                    if (error != null)
                    {
                        Directory.Delete(destFullDirectory);
                        return Json(new { error = error });
                    }
                    string rootHost = Request.Url.Scheme + "://" + Request.Url.Authority;
                    
                    WebLMSThread.StartBackgroundThread(() =>
                    {
                        Exception ex = null;
                        Models.File file = null;
                        try
                        {
                            string outFilePath = converter.Start();
                            file = new Models.File();
                            file.Md5Hash = hash;
                            file.FilePath = "/TempVideoFiles/" + email + "/" + hash + "/video/" + Path.GetFileName(outFilePath); // Path.Combine(videoDirectory, Path.GetFileName(outFilePath));
                            file.EmailWhoConverted = email;
                            file.Datetime = DateTime.Now;
                            _db.Files.Add(file);
                            _db.SaveChanges();
                            //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "saved.txt"), "Saved!");
                        }
                        catch (Exception e)
                        {
                            //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "catch.txt"), "Catch!");
                            ex = e;
                        }
                        finally
                        {
                            //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "finally.txt"), "Finally!");

                            //string messageFor = "Ссылка на скачивание видеофайла: " + Request.Url.Authority + "/Home/GetVideoFile/?hash=" + hash + "&id=" + file.Id + " \r\nПосле разового скачивания файл удалится с сервера.\r\nС уважением, компания WebLMS.\r\nhttp://weblms.ru";
                            //string messageFor = "Ссылка на скачивание видеофайла: /Home/GetVideoFile/?hash=" + hash + "&id=" + file.Id + " После разового скачивания файл удалится с сервера.С уважением, компания WebLMS";
                            string messageFor = ex == null && file != null ?
                            String.Format("Ссылка на скачивание видеофайла: {0}/Home/GetVideoFile/?hash={1}&id={2} \r\nПосле разового скачивания файл удалится с сервера.\r\nС уважением, компания WebLMS.\r\nhttp://weblms.ru", rootHost, hash, file.Id) : 
                            String.Format("Произошла ошибка при конвертации файла, попробуйте повторить операцию позже. Детали ошибки: \r\n{0}.\r\nС уважением, компания WebLMS.\r\nhttp://weblms.ru", ex.Message);

                            //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "finally1.txt"), "Finally1!");
                            try
                            {
                                //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "finallyTry.txt"), "finallyTry!");
                                //ISender fileSender1 = new FileSender();
                                //fileSender1.SendFileLink(Path.Combine(destFullDirectory, "input.txt1"), "Отправляется!");

                                ISender emailSender = new EmailSender();
                                emailSender.SendFileLink(email, messageFor);

                                //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "email.txt"), messageFor);
                                //fileSender1.SendFileLink(Path.Combine(destFullDirectory, "input.txt2"), "Отправлено!");
                            }
                            catch (Exception e)
                            {
                                //System.IO.File.WriteAllText(Path.Combine(destFullDirectory, "input3.txt"), e.Message);

                                //ISender fileSender = new FileSender();
                                //fileSender.SendFileLink(Path.Combine(destFullDirectory, "input3.txt"), e.Message);
                            }
                            
                        }
                        
                    });
                    return Json(
                        new
                        {
                            status = "process",
                            message = "Файл проходит обработку. Ссылку на скачивание Вы получите по почте!"
                        }
                    );
                }
                catch (Exception e)
                {
                    return Json(new { status = "error", error = "Произошла ошибка при извлечении архива, повторите попытку позже!" });
                }
            }
        }
    }
}
